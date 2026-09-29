using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using System.Reactive;
using System.Reactive.Linq;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.JsonRpc;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;

using Miningcore.VarDiff;

namespace Miningcore.Blockchain.Quantus;

[CoinFamily(CoinFamily.Quantus)]
public class QuantusPool : PoolBase
{
    public QuantusPool(
        IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf,
        IStatsRepository statsRepo,
        IMapper mapper,
        IMasterClock clock,
        IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm,
        NicehashService nicehashService) :
        base(
            ctx,
            serializerSettings,
            cf,
            statsRepo,
            mapper,
            clock,
            messageBus,
            rmsm,
            nicehashService)
    {
    }

    private QuantusJobManager manager;
    private QuantusCoinTemplate coin;
    private QuantusPoolConfigExtra extraPoolConfig;

    private QuantusPoolQuicServer quicServer;

    private BigInteger poolDifficulty;
    private double poolDifficultyDouble;
    private VarDiffConfig varDiffConfig;

    private readonly ConcurrentDictionary<long, WorkerContextBase>
        quantusContexts = new();

    public override void Configure(
        PoolConfig pc,
        ClusterConfig cc)
    {
        coin =
            pc.Template.As<QuantusCoinTemplate>();

        extraPoolConfig =
            pc.Extra
                .SafeExtensionDataAs<
                    QuantusPoolConfigExtra>()
            ?? new QuantusPoolConfigExtra();

        base.Configure(
            pc,
            cc);
    }

    protected override async Task InitStatsAsync(
        CancellationToken ct)
    {
        // Let PoolBase restore persisted pool statistics such as
        // historical pool hashrate, miner count, etc.
        await base.InitStatsAsync(ct);

        // PoolBase.LoadStatsAsync replaces blockchainStats with a
        // database snapshot. QuantusJobManager owns the authoritative
        // live network stats, so restore that object after DB loading.
        blockchainStats =
            manager.BlockchainStats;
    }

    protected override async Task SetupJobManager(
        CancellationToken ct)
    {
        QuantusPow.EnsureAvailable();

        if(poolConfig.EnableInternalStratum == true)
        {
            throw new PoolStartupException(
                "Quantus uses native QUIC mining. " +
                "Set enableInternalStratum to false.",
                poolConfig.Id);
        }

        //
        // Quantus currently exposes one native QUIC mining
        // endpoint per Miningcore pool.
        //
        if(poolConfig.Ports == null ||
           poolConfig.Ports.Count != 1)
        {
            throw new PoolStartupException(
                "Quantus currently requires exactly one mining port",
                poolConfig.Id);
        }

        var portConfig =
            poolConfig.Ports.Single();

        var port =
            portConfig.Key;

        var endpoint =
            portConfig.Value;

        if(endpoint.Difficulty <= 0 ||
           double.IsNaN(endpoint.Difficulty) ||
           double.IsInfinity(endpoint.Difficulty))
        {
            throw new PoolStartupException(
                "Quantus pool difficulty must be greater than zero",
                poolConfig.Id);
        }

        //
        // Quantus mining difficulty is U512 integer difficulty.
        // Do not silently truncate Miningcore floating-point diff.
        //
        if(Math.Truncate(endpoint.Difficulty) !=
           endpoint.Difficulty)
        {
            throw new PoolStartupException(
                "Quantus pool difficulty must be an integer",
                poolConfig.Id);
        }

        varDiffConfig =
            endpoint.VarDiff;

        if(varDiffConfig != null)
        {
            if(varDiffConfig.MinDiff <= 0 ||
               Math.Truncate(varDiffConfig.MinDiff) != varDiffConfig.MinDiff)
            {
                throw new PoolStartupException(
                    "Quantus VarDiff minDiff must be a positive integer",
                    poolConfig.Id);
            }

            if(varDiffConfig.MaxDiff is { } maxDiff &&
               (maxDiff <= 0 ||
                Math.Truncate(maxDiff) != maxDiff))
            {
                throw new PoolStartupException(
                    "Quantus VarDiff maxDiff must be a positive integer",
                    poolConfig.Id);
            }
        }

        poolDifficultyDouble =
            endpoint.Difficulty;

        poolDifficulty =
            new BigInteger(
                endpoint.Difficulty);

        if(string.IsNullOrWhiteSpace(
            extraPoolConfig.TlsPfxPath))
        {
            throw new PoolStartupException(
                "Quantus tlsPfxPath is not configured",
                poolConfig.Id);
        }

        if(!File.Exists(
            extraPoolConfig.TlsPfxPath))
        {
            throw new PoolStartupException(
                $"Quantus TLS PFX does not exist: " +
                $"{extraPoolConfig.TlsPfxPath}",
                poolConfig.Id);
        }

        //
        // Start the actual Quantus upstream manager.
        //
        manager =
            ctx.Resolve<QuantusJobManager>();

        manager.Configure(
            poolConfig,
            clusterConfig);

        await manager.StartAsync(
            ct);

        //
        // PoolBase/API should expose the same stats object
        // maintained by QuantusJobManager.
        //
        blockchainStats =
            manager.BlockchainStats;

        //
        // Determine the QUIC listen address from the standard
        // Miningcore port config.
        //
        IPAddress listenAddress;

        if(string.IsNullOrWhiteSpace(
            endpoint.ListenAddress))
        {
            listenAddress =
                IPAddress.Loopback;
        }
        else if(endpoint.ListenAddress == "*")
        {
            listenAddress =
                IPAddress.Any;
        }
        else if(!IPAddress.TryParse(
            endpoint.ListenAddress,
            out listenAddress))
        {
            throw new PoolStartupException(
                $"Invalid Quantus listen address: " +
                $"{endpoint.ListenAddress}",
                poolConfig.Id);
        }

        //
        // Start public/miner-facing QUIC server.
        //
        quicServer =
            new QuantusPoolQuicServer(
                poolDifficulty,
                extraPoolConfig.TlsPfxPath);

        quicServer.MinerConnected +=
            OnQuantusMinerConnected;

        quicServer.MinerDisconnected +=
            OnQuantusMinerDisconnected;

        quicServer.ShareReceived +=
            share =>
                OnQuantusShareAsync(
                    share,
                    ct);

        await quicServer.StartAsync(
            listenAddress,
            port,
            ct);

        if(minerStatsRedis.Enabled)
        {
            _ = Task.Run(
                () => RunQuantusRedisHeartbeatAsync(ct),
                CancellationToken.None);
        }

        if(varDiffConfig != null)
        {
            _ = Task.Run(
                () => RunQuantusVarDiffIdleUpdaterAsync(
                    poolConfig.VardiffIdleSweepInterval ?? 30,
                    ct),
                CancellationToken.None);
        }

        logger.Info(
            $"Quantus QUIC mining server listening on " +
            $"{listenAddress}:{port} " +
            $"at difficulty {poolDifficulty}");

        //
        // ReplaySubject inside QuantusJobManager immediately
        // sends the latest job to this subscription.
        //
        disposables.Add(
            manager.Jobs
                .Select(job =>
                    Observable.FromAsync(
                        () =>
                            quicServer
                                .SetCurrentJobAsync(
                                    job,
                                    ct)))
                .Concat()
                .Subscribe(
                    _ => { },
                    ex =>
                        logger.Error(
                            ex,
                            "Quantus job broadcast failed")));

        //
        // Dispose the QUIC server during pool shutdown.
        //
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(
                        Timeout.Infinite,
                        ct);
                }
                catch(OperationCanceledException)
                {
                }
                finally
                {
                    try
                    {
                        if(quicServer != null)
                            await quicServer.DisposeAsync();
                    }
                    catch(Exception ex)
                    {
                        logger.Debug(
                            ex,
                            "Error stopping Quantus QUIC server");
                    }
                }
            },
            CancellationToken.None);
    }

    private void OnQuantusMinerConnected(
        QuantusPoolMiner miner)
    {
        var context =
            CreateWorkerContext();

        context.Init(
            poolDifficultyDouble,
            varDiffConfig,
            clock);

        context.Miner =
            miner.Wallet;

        context.Worker =
            miner.Worker;

        context.UserAgent =
            "Quantus QUIC";

        context.IsAuthorized =
            true;

        context.IsSubscribed =
            true;

        if(minerStatsRedis.Enabled)
        {
            context.RedisSessionId =
                minerStatsRedis.CreateSessionId(
                    $"quantus-{miner.MinerId}");
        }

        if(quantusContexts.TryAdd(
            miner.MinerId,
            context))
        {
            if(minerStatsRedis.Enabled)
            {
                minerStatsRedis.RecordConnected(
                    poolConfig.Id,
                    context);
            }
        }
    }

    private void OnQuantusMinerDisconnected(
        QuantusPoolMiner miner)
    {
        if(quantusContexts.TryRemove(
            miner.MinerId,
            out var context))
        {
            context.LastActivity =
                clock.Now;

            if(minerStatsRedis.Enabled &&
               !string.IsNullOrWhiteSpace(
                   context.RedisSessionId))
            {
                minerStatsRedis.RecordDisconnected(
                    poolConfig.Id,
                    context);
            }
        }
    }

    private void TouchQuantusContext(
        long minerId)
    {
        if(quantusContexts.TryGetValue(
            minerId,
            out var context))
        {
            context.LastActivity =
                clock.Now;
        }
    }

    private async Task RunQuantusRedisHeartbeatAsync(
        CancellationToken ct)
    {
        try
        {
            using var timer =
                new PeriodicTimer(
                    minerStatsRedis.SessionHeartbeatInterval);

            while(await timer.WaitForNextTickAsync(ct))
            {
                foreach(var context in
                    quantusContexts.Values)
                {
                    context.LastActivity =
                        clock.Now;

                    minerStatsRedis.RecordHeartbeat(
                        poolConfig.Id,
                        context);
                }
            }
        }
        catch(OperationCanceledException)
            when(ct.IsCancellationRequested)
        {
        }
    }

    private async Task RunQuantusVarDiffIdleUpdaterAsync(
        int interval,
        CancellationToken ct)
    {
        try
        {
            using var timer =
                new PeriodicTimer(
                    TimeSpan.FromSeconds(
                        Math.Max(1, interval)));

            while(await timer.WaitForNextTickAsync(ct))
            {
                foreach(var entry in
                    quantusContexts.ToArray())
                {
                    try
                    {
                        await UpdateQuantusVarDiffIdleAsync(
                            entry.Key,
                            entry.Value,
                            ct);
                    }
                    catch(OperationCanceledException)
                        when(ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch(Exception ex)
                    {
                        logger.Error(
                            ex,
                            $"Quantus idle VarDiff failed for " +
                            $"{entry.Value?.Miner}.{entry.Value?.Worker}");
                    }
                }
            }
        }
        catch(OperationCanceledException)
            when(ct.IsCancellationRequested)
        {
        }
    }

    private async Task UpdateQuantusVarDiffIdleAsync(
        long minerId,
        WorkerContextBase context,
        CancellationToken ct)
    {
        if(varDiffConfig == null ||
           context?.VarDiff == null)
        {
            return;
        }

        var newDiff =
            VarDiffManager.IdleUpdate(
                context,
                varDiffConfig,
                clock);

        if(!newDiff.HasValue)
            return;

        var quantized =
            Math.Round(
                newDiff.Value,
                MidpointRounding.AwayFromZero);

        quantized =
            Math.Max(
                varDiffConfig.MinDiff,
                quantized);

        if(varDiffConfig.MaxDiff.HasValue)
        {
            quantized =
                Math.Min(
                    varDiffConfig.MaxDiff.Value,
                    quantized);
        }

        quantized =
            Math.Max(
                1d,
                quantized);

        if(quantized == context.Difficulty)
            return;

        var oldDiff =
            context.Difficulty;

        var qpowDifficulty =
            new BigInteger(
                quantized);

        //
        // First update the QUIC session's difficulty for
        // future jobs.
        //
        if(!quicServer.SetMinerDifficulty(
            minerId,
            qpowDifficulty))
        {
            return;
        }

        //
        // Keep Miningcore's worker context in sync before
        // the replacement job is sent.
        //
        context.SetDifficulty(
            quantized);

        if(minerStatsRedis.Enabled)
        {
            minerStatsRedis.RecordRetarget(
                poolConfig.Id,
                context,
                quantized);
        }

        logger.Info(
            $"Quantus miner " +
            $"{context.Miner}.{context.Worker} " +
            $"VarDiff {oldDiff:0} -> {quantized:0} [IDLE]");

        //
        // Quantus miners keep mining the current job until
        // they receive another NewJob. Send one immediately
        // so the lower idle difficulty actually takes effect.
        //
        await quicServer.IssueMinerJobAsync(
            minerId,
            ct);
    }

    private void UpdateQuantusVarDiff(
        long minerId,
        WorkerContextBase context)
    {
        if(varDiffConfig == null ||
           context?.VarDiff == null)
        {
            return;
        }

        var newDiff =
            VarDiffManager.Update(
                context,
                varDiffConfig,
                clock);

        if(!newDiff.HasValue)
            return;

        var quantized =
            Math.Round(
                newDiff.Value,
                MidpointRounding.AwayFromZero);

        quantized =
            Math.Max(
                varDiffConfig.MinDiff,
                quantized);

        if(varDiffConfig.MaxDiff.HasValue)
        {
            quantized =
                Math.Min(
                    varDiffConfig.MaxDiff.Value,
                    quantized);
        }

        quantized =
            Math.Max(
                1d,
                quantized);

        if(quantized == context.Difficulty)
            return;

        var oldDiff =
            context.Difficulty;

        var qpowDifficulty =
            new BigInteger(
                quantized);

        //
        // The submitted share has already been validated
        // against AssignedDifficulty. This changes only the
        // difficulty used for the miner's NEXT QUIC job.
        //
        if(!quicServer.SetMinerDifficulty(
            minerId,
            qpowDifficulty))
        {
            return;
        }

        context.SetDifficulty(
            quantized);

        if(minerStatsRedis.Enabled)
        {
            minerStatsRedis.RecordRetarget(
                poolConfig.Id,
                context,
                quantized);
        }

        logger.Info(
            $"Quantus miner " +
            $"{context.Miner}.{context.Worker} " +
            $"VarDiff {oldDiff:0} -> {quantized:0}");
    }

    private async Task OnQuantusShareAsync(
        QuantusPoolShare poolShare,
        CancellationToken ct)
    {
        var validation =
            poolShare.Validation;

        TouchQuantusContext(
            poolShare.MinerId);

        if(!validation.Accepted)
        {
            if(quantusContexts.TryGetValue(
                poolShare.MinerId,
                out var rejectedContext))
            {
                rejectedContext.Stats.InvalidShares++;

                if(minerStatsRedis.Enabled)
                {
                    minerStatsRedis.RecordRejected(
                        poolConfig.Id,
                        rejectedContext,
                        (double) poolShare.AssignedDifficulty);
                }
            }

            logger.Debug(
                $"Rejected Quantus share " +
                $"{poolShare.Login}: " +
                $"{validation.Error}");

            return;
        }

        var job =
            poolShare.Job;

        var blockCandidate =
            validation.BlockCandidate;


        //
        // If this meets full network difficulty, forward the
        // solution to the real Quantus node.
        //
        // IMPORTANT:
        // Forwarding is not itself proof that the block was
        // imported. Persist it as Pending with the exact work
        // so accounting can prove canonical inclusion.
        //
        if(blockCandidate)
        {
            try
            {
                var forwarded =
                    await manager
                        .SubmitNetworkCandidateAsync(
                            job,
                            poolShare.Submission,
                            ct);

                if(forwarded)
                {

                    logger.Info(
                        $"Quantus network solution forwarded: " +
                        $"height={job.Height}, " +
                        $"miner={poolShare.Wallet}, " +
                        $"worker={poolShare.Worker}");
                }
                else
                {
                    logger.Info(
                        $"Quantus candidate became stale before forwarding: " +
                        $"height={job.Height}");
                }
            }
            catch(Exception ex)
            {
                logger.Error(
                    ex,
                    $"Failed forwarding Quantus network candidate " +
                    $"for height {job.Height}");
            }
        }

        //
        // Persist the accepted pool share through Miningcore's
        // normal message bus / ShareRecorder pipeline.
        //
        var share =
            new Miningcore.Blockchain.Share
            {
                PoolId =
                    poolConfig.Id,

                Miner =
                    poolShare.Wallet,

                Worker =
                    poolShare.Worker,

                UserAgent =
                    "Quantus QUIC",

                IpAddress =
                    poolShare.IpAddress,

                Source =
                    "quantus-quic",

                Difficulty =
                    (double) BigInteger.Min(poolShare.AssignedDifficulty, job.NetworkDifficulty),

                BlockHeight =
                    job.Height,

                //
                // Network solutions, including uncertain sends, are persisted as
                // Pending blocks. QuantusPayoutHandler will later
                // verify the canonical MiningRewards.MinerRewarded
                // event before confirming or orphaning the block.
                //
                IsBlockCandidate =
                    blockCandidate,

                TransactionConfirmationData = blockCandidate
                    ? QuantusCandidate.Create(job, poolShare.Submission.Work).Serialize() : null,
                BlockHash = blockCandidate
                    ? QuantusCandidate.Create(job, poolShare.Submission.Work).Id : null,

                NetworkDifficulty =
                    (double)
                    job.NetworkDifficulty,

                Created =
                    clock.Now
            };

        messageBus.SendMessage(
            share);

        if(quantusContexts.TryGetValue(
            poolShare.MinerId,
            out var acceptedContext))
        {
            acceptedContext.Stats.ValidShares++;

            UpdateQuantusVarDiff(
                poolShare.MinerId,
                acceptedContext);
        }

        logger.Debug(
            $"Accepted Quantus share: " +
            $"miner={poolShare.Wallet}, " +
            $"worker={poolShare.Worker}, " +
            $"height={job.Height}, " +
            $"diff={poolShare.AssignedDifficulty}");
    }

    //
    // Quantus difficulty directly represents the expected
    // number of QPoW hashes required for one accepted share.
    //
    public override double HashrateFromShares(
        double shares,
        double interval)
    {
        if(interval <= 0)
            return 0;

        return shares / interval;
    }

    public override double ShareMultiplier =>
        1;

    //
    // PoolBase requires this even though Quantus miners do
    // not use Miningcore's TCP StratumConnection.
    //
    protected override Task OnRequestAsync(
        StratumConnection connection,
        Timestamped<JsonRpcRequest> tsRequest,
        CancellationToken ct)
    {
        throw new StratumException(
            StratumError.MinusOne,
            "Quantus uses the native QUIC mining protocol");
    }

    protected override WorkerContextBase
        CreateWorkerContext()
    {
        return new WorkerContextBase();
    }
}
