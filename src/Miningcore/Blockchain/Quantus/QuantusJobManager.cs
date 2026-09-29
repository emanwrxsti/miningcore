using System.Numerics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Autofac;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Time;

namespace Miningcore.Blockchain.Quantus;

public class QuantusJobManager : JobManagerBase<QuantusJob>
{
    public QuantusJobManager(
        IComponentContext ctx,
        IMessageBus messageBus,
        IMasterClock clock) :
        base(ctx, messageBus)
    {
        this.clock =
            clock ?? throw new ArgumentNullException(
                nameof(clock));
    }

    private readonly IMasterClock clock;

    private readonly ReplaySubject<QuantusJob> jobs =
        new(1);

    private readonly TaskCompletionSource<QuantusJob>
        initialJob =
            new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);

    private DaemonEndpointConfig daemonEndpoint;

    private QuantusDaemonEndpointConfigExtra
        extraDaemonConfig;

    private QuantusCoinTemplate coin;

    private QuantusRpcClient rpc;

    private QuantusQuicClient upstream;

    private CancellationToken lifetimeToken;

    public IObservable<QuantusJob> Jobs =>
        jobs.AsObservable();

    public BlockchainStats BlockchainStats { get; } =
        new();

    public string Network =>
        BlockchainStats.NetworkType;

    public QuantusCoinTemplate Coin =>
        coin;

    public override void Configure(
        PoolConfig pc,
        ClusterConfig cc)
    {
        coin =
            pc.Template.As<QuantusCoinTemplate>();

        daemonEndpoint =
            pc.Daemons?
                .FirstOrDefault(
                    x =>
                        string.IsNullOrEmpty(
                            x.Category));

        if(daemonEndpoint == null)
        {
            throw new PoolStartupException(
                "Quantus daemon endpoint is not configured",
                pc.Id);
        }

        extraDaemonConfig =
            daemonEndpoint.Extra
                .SafeExtensionDataAs<
                    QuantusDaemonEndpointConfigExtra>()
            ?? new QuantusDaemonEndpointConfigExtra();

        base.Configure(
            pc,
            cc);
    }

    protected override void ConfigureDaemons()
    {
        var scheme =
            daemonEndpoint.Ssl
                ? "https"
                : "http";

        var path =
            daemonEndpoint.HttpPath;

        if(string.IsNullOrWhiteSpace(path))
            path = "/";

        if(!path.StartsWith('/'))
            path = "/" + path;

        if(!path.EndsWith('/'))
            path += "/";

        var endpoint =
            $"{scheme}://" +
            $"{daemonEndpoint.Host}:" +
            $"{daemonEndpoint.Port}" +
            $"{path}";

        rpc =
            new QuantusRpcClient(
                endpoint);
    }

    protected override async Task<bool>
        AreDaemonsHealthyAsync(
            CancellationToken ct)
    {
        try
        {
            var health =
                await rpc.GetHealthAsync(ct);

            var version =
                await rpc.GetVersionAsync(ct);

            BlockchainStats.NodeVersion =
                version;

            BlockchainStats.ConnectedPeers =
                health.Peers;

            return true;
        }
        catch(Exception ex)
        {
            logger.Debug(
                ex,
                "Quantus RPC health check failed");

            return false;
        }
    }

    protected override async Task<bool>
        AreDaemonsConnectedAsync(
            CancellationToken ct)
    {
        try
        {
            var health =
                await rpc.GetHealthAsync(ct);

            BlockchainStats.ConnectedPeers =
                health.Peers;

            //
            // Substrate's system_health tells us whether
            // this node is expected to have peers.
            //
            // Don't start pool mining against an isolated
            // mainnet node.
            //
            if(health.ShouldHavePeers &&
               health.Peers <= 0)
            {
                return false;
            }

            return true;
        }
        catch(Exception ex)
        {
            logger.Debug(
                ex,
                "Quantus peer check failed");

            return false;
        }
    }

    protected override async Task
        EnsureDaemonsSynchedAsync(
            CancellationToken ct)
    {
        using var timer =
            new PeriodicTimer(
                TimeSpan.FromSeconds(5));

        var pendingMessageShown =
            false;

        while(true)
        {
            var health =
                await rpc.GetHealthAsync(ct);

            BlockchainStats.ConnectedPeers =
                health.Peers;

            if(!health.IsSyncing)
            {
                logger.Info(
                    "Quantus daemon is synced with blockchain");

                return;
            }

            if(!pendingMessageShown)
            {
                logger.Info(
                    "Quantus daemon is still syncing. " +
                    "Job manager will start once synced.");

                pendingMessageShown =
                    true;
            }

            if(!await timer.WaitForNextTickAsync(ct))
                return;
        }
    }

    protected override async Task
        PostStartInitAsync(
            CancellationToken ct)
    {
        lifetimeToken =
            ct;

        QuantusPow.EnsureAvailable();
        if(extraDaemonConfig.MaxJobAgeSeconds < 30 || extraDaemonConfig.MaxJobAgeSeconds > 3600)
            throw new PoolStartupException("Quantus maxJobAgeSeconds must be 30..3600", poolConfig.Id);

        if(string.IsNullOrWhiteSpace(
            extraDaemonConfig.AuthTokenPath))
        {
            throw new PoolStartupException(
                "Quantus daemon authTokenPath is not configured",
                poolConfig.Id);
        }

        if(string.IsNullOrWhiteSpace(
            extraDaemonConfig.TlsFingerprintPath))
        {
            throw new PoolStartupException(
                "Quantus daemon tlsFingerprintPath is not configured",
                poolConfig.Id);
        }

        if(!File.Exists(
            extraDaemonConfig.AuthTokenPath))
        {
            throw new PoolStartupException(
                "Quantus miner auth-token file does not exist",
                poolConfig.Id);
        }

        if(!File.Exists(
            extraDaemonConfig.TlsFingerprintPath))
        {
            throw new PoolStartupException(
                "Quantus miner TLS fingerprint file does not exist",
                poolConfig.Id);
        }

        BlockchainStats.NetworkType =
            string.IsNullOrWhiteSpace(
                extraDaemonConfig.Network)
                ? "mainnet"
                : extraDaemonConfig.Network;

        BlockchainStats.RewardType =
            "POW";

        await UpdateNetworkStatsAsync(
            ct);

        _ = Task.Run(() => SuperviseUpstreamAsync(ct), CancellationToken.None);

        //
        // Don't declare the manager online until
        // the node has actually supplied mining work.
        //
        await initialJob.Task.WaitAsync(
            ct);

        _ = Task.Run(
            () => NetworkStatsLoopAsync(ct),
            CancellationToken.None);
    }

    private DateTime lastJobReceived;

    private async Task SuperviseUpstreamAsync(CancellationToken ct)
    {
        var retrySeconds = 1;
        while(!ct.IsCancellationRequested)
        {
            QuantusQuicClient client = null;
            try
            {
                client = new QuantusQuicClient(daemonEndpoint.Host,
                    extraDaemonConfig.MinerPort ?? QuantusConstants.DefaultMinerPort,
                    extraDaemonConfig.AuthTokenPath, extraDaemonConfig.TlsFingerprintPath);
                client.JobReceived += OnUpstreamJobAsync;
                upstream = client;
                await client.ConnectAsync(ct);
                lastJobReceived = DateTime.UtcNow;
                while(!ct.IsCancellationRequested)
                {
                    var completed = await Task.WhenAny(client.Completion, Task.Delay(TimeSpan.FromSeconds(5), ct));
                    if(completed == client.Completion)
                    {
                        await client.Completion;
                        throw new IOException("Quantus upstream closed");
                    }
                    ct.ThrowIfCancellationRequested();
                    var health = await rpc.GetHealthAsync(ct);
                    if(health.IsSyncing || (health.ShouldHavePeers && health.Peers == 0))
                        throw new IOException("Quantus node is syncing or isolated");
                    if(DateTime.UtcNow - lastJobReceived > TimeSpan.FromSeconds(extraDaemonConfig.MaxJobAgeSeconds))
                        throw new IOException("Quantus upstream work expired");
                    retrySeconds = 1;
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
            catch(Exception ex) { logger.Warn(ex, "Quantus upstream unavailable; invalidating work and reconnecting"); }
            finally
            {
                lock(jobLock) { currentJob = null; hasInitialBlockTemplate = false; }
                jobs.OnNext(null);
                upstream = null;
                if(client != null)
                {
                    try { await client.DisposeAsync(); } catch(Exception ex) { logger.Debug(ex, "Quantus upstream cleanup"); }
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(retrySeconds), ct); }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
            retrySeconds = Math.Min(30, retrySeconds * 2);
        }
    }

    private async Task OnUpstreamJobAsync(
        QuantusMiningRequest request)
    {
        try
        {
            var tipLong =
                await rpc.GetTipHeightAsync(
                    lifetimeToken);

            var tip =
                checked((ulong) tipLong);

            var height =
                checked(tipLong + 1);

            var job =
                new QuantusJob(
                    request,
                    height);

            lock(jobLock)
            {
                if(tip >
                   BlockchainStats.BlockHeight)
                {
                    BlockchainStats
                        .LastNetworkBlockTime =
                        clock.Now;
                }

                BlockchainStats.BlockHeight =
                    tip;

                BlockchainStats.NetworkDifficulty =
                    (double)
                    job.NetworkDifficulty;

                // Quantus QPoW difficulty represents the expected
                // number of hashes required to solve a block.
                // Current mainnet target block time is 12 seconds.
                BlockchainStats.NetworkHashrate =
                    BlockchainStats.NetworkDifficulty / 12d;

                currentJob =
                    job;

                hasInitialBlockTemplate =
                    true;
            }

            logger.Info(
                $"Quantus job {job.UpstreamJobId}: " +
                $"height={job.Height}, " +
                $"difficulty={job.NetworkDifficulty}");

            lastJobReceived = DateTime.UtcNow;
            jobs.OnNext(
                job);

            initialJob.TrySetResult(
                job);
        }
        catch(OperationCanceledException)
            when(lifetimeToken.IsCancellationRequested)
        {
        }
        catch(Exception ex)
        {
            logger.Error(
                ex,
                "Unable to process Quantus mining job");
            throw; // Supervisor invalidates work and reconnects.
        }
    }

    private async Task
        NetworkStatsLoopAsync(
            CancellationToken ct)
    {
        try
        {
            using var timer =
                new PeriodicTimer(
                    TimeSpan.FromMinutes(1));

            while(await timer
                .WaitForNextTickAsync(ct))
            {
                try
                {
                    await UpdateNetworkStatsAsync(
                        ct);
                }
                catch(OperationCanceledException)
                    when(ct.IsCancellationRequested)
                {
                    break;
                }
                catch(Exception ex)
                {
                    logger.Debug(
                        ex,
                        "Quantus network stats update failed");
                }
            }
        }
        catch(OperationCanceledException)
            when(ct.IsCancellationRequested)
        {
        }
    }

    private async Task
        UpdateNetworkStatsAsync(
            CancellationToken ct)
    {
        var tipLong =
            await rpc.GetTipHeightAsync(
                ct);

        var tip =
            checked((ulong) tipLong);

        var health =
            await rpc.GetHealthAsync(
                ct);

        var version =
            await rpc.GetVersionAsync(
                ct);

        BlockchainStats.BlockHeight =
            tip;

        BlockchainStats.ConnectedPeers =
            health.Peers;

        BlockchainStats.NodeVersion =
            version;

        lock(jobLock)
        {
            if(currentJob != null)
            {
                BlockchainStats.NetworkDifficulty =
                    (double)
                    currentJob.NetworkDifficulty;

                // Estimated Quantus network hashrate from the
                // current QPoW difficulty and 12-second block target.
                BlockchainStats.NetworkHashrate =
                    BlockchainStats.NetworkDifficulty / 12d;
            }
        }
    }

    public async Task<bool>
        SubmitNetworkCandidateAsync(
            QuantusJob job,
            QuantusMiningResult minerResult,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(
            job);

        ArgumentNullException.ThrowIfNull(
            minerResult);

        if(string.IsNullOrWhiteSpace(
            minerResult.Work))
        {
            return false;
        }

        QuantusJob active;

        lock(jobLock)
            active = currentJob;

        //
        // Never forward a solution for a superseded
        // upstream mining template.
        //
        if(active == null ||
           !ReferenceEquals(
               active,
               job))
        {
            return false;
        }

        var upstreamResult =
            new QuantusMiningResult
            {
                Status =
                    "completed",

                JobId =
                    job.UpstreamJobId,

                Nonce =
                    minerResult.Nonce,

                Work =
                    minerResult.Work,

                HashCount =
                    minerResult.HashCount,

                ElapsedTime =
                    minerResult.ElapsedTime
            };

        var client = upstream;
        if(client == null || !client.IsConnected) return false;
        upstreamResult.Work = QuantusEncoding.Hex(minerResult.Work, 64);
        using var submitTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        submitTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        await client.SubmitResultAsync(upstreamResult, submitTimeout.Token);

        logger.Info(
            $"Submitted Quantus network candidate " +
            $"for height {job.Height}, " +
            $"upstream job {job.UpstreamJobId}");

        return true;
    }

    public override QuantusJob
        GetJobForStratum()
    {
        lock(jobLock)
            return currentJob;
    }
}
