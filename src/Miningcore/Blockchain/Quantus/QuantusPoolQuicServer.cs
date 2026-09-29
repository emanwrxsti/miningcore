using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Numerics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Miningcore.Blockchain.Quantus;

public sealed class QuantusPoolQuicServer : IAsyncDisposable
{
    private readonly BigInteger poolDifficulty;
    private readonly X509Certificate2 certificate;

    private readonly ConcurrentDictionary<long, MinerSession> sessions = new();

    private readonly object jobLock = new();

    private CancellationTokenSource cts;
    private QuicListener listener;
    private Task acceptTask;

    private QuantusJob currentJob;

    private readonly SemaphoreSlim connectionSlots = new(512, 512);

    private long nextMinerId;
    private long nextDownstreamJobId;

    public QuantusPoolQuicServer(
        BigInteger poolDifficulty,
        string pfxPath)
    {
        if(poolDifficulty <= BigInteger.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(poolDifficulty));

        this.poolDifficulty = poolDifficulty;

        certificate = new X509Certificate2(
            pfxPath,
            string.Empty,
            X509KeyStorageFlags.EphemeralKeySet);
    }

    public event Action<QuantusPoolMiner> MinerConnected;
    public event Action<QuantusPoolMiner> MinerDisconnected;

    public event Func<QuantusPoolShare, Task> ShareReceived;

    public IPEndPoint LocalEndPoint =>
        listener?.LocalEndPoint as IPEndPoint;

    public bool SetMinerDifficulty(
        long minerId,
        BigInteger difficulty)
    {
        if(difficulty <= BigInteger.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(difficulty));

        if(!sessions.TryGetValue(
            minerId,
            out var miner))
        {
            return false;
        }

        lock(miner.StateLock)
            miner.CurrentDifficulty = difficulty;

        return true;
    }

    public async Task<bool> IssueMinerJobAsync(
        long minerId,
        CancellationToken ct = default)
    {
        if(!sessions.TryGetValue(
            minerId,
            out var miner))
        {
            return false;
        }

        await IssueJobAsync(
            miner,
            ct);

        return true;
    }

    public async Task StartAsync(
        IPAddress bindAddress,
        int port,
        CancellationToken ct = default)
    {
        if(!QuicListener.IsSupported)
            throw new PlatformNotSupportedException(
                "QUIC server is not supported. Ensure libmsquic is installed.");

        var serverOptions =
            new QuicServerConnectionOptions
            {
                IdleTimeout = TimeSpan.FromSeconds(90),
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,

                MaxInboundBidirectionalStreams = 1,
                MaxInboundUnidirectionalStreams = 0,

                ServerAuthenticationOptions =
                    new SslServerAuthenticationOptions
                    {
                        ApplicationProtocols =
                        [
                            new SslApplicationProtocol(
                                QuantusConstants.MinerAlpn)
                        ],

                        ServerCertificate = certificate
                    }
            };

        var listenerOptions =
            new QuicListenerOptions
            {
                ListenEndPoint =
                    new IPEndPoint(bindAddress, port),

                ApplicationProtocols =
                [
                    new SslApplicationProtocol(
                        QuantusConstants.MinerAlpn)
                ],

                ConnectionOptionsCallback =
                    (_, _, _) =>
                        ValueTask.FromResult(
                            serverOptions)
            };

        listener =
            await QuicListener.ListenAsync(
                listenerOptions,
                ct);

        cts =
            CancellationTokenSource
                .CreateLinkedTokenSource(ct);

        acceptTask =
            AcceptLoopAsync(cts.Token);
    }

    public async Task SetCurrentJobAsync(
        QuantusJob job,
        CancellationToken ct = default)
    {
        lock(jobLock)
            currentJob = job;

        var miners = sessions.Values.ToArray();
        if(job == null)
        {
            foreach(var miner in miners) miner.Stop();
            return;
        }
        // One stalled miner must not delay the next job for every other miner.
        await Task.WhenAll(miners.Select(async miner =>
        {
            try { await IssueJobAsync(miner, ct); }
            catch { miner.Stop(); }
        }));
    }

    private async Task AcceptLoopAsync(
        CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            QuicConnection connection;

            try
            {
                connection =
                    await listener
                        .AcceptConnectionAsync(ct);
            }
            catch(OperationCanceledException)
                when(ct.IsCancellationRequested)
            {
                break;
            }
            catch(QuicException) when(!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                continue; // A peer failing its handshake cannot stop the listener.
            }


            if(!connectionSlots.Wait(0))
            {
                await connection.DisposeAsync();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleConnectionAsync(connection, ct); }
                finally { connectionSlots.Release(); }
            }, CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(
        QuicConnection connection,
        CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = lifetime.Token;
        await using(connection)
        {
            QuicStream stream = null;
            MinerSession session = null;

            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                stream = await connection.AcceptInboundStreamAsync(handshake.Token);

                var firstMessage =
                    await ReadMessageAsync(
                        stream,
                        handshake.Token);

                using(var document =
                    JsonDocument.Parse(firstMessage))
                {
                    if(!document.RootElement
                        .TryGetProperty(
                            "Ready",
                            out var ready))
                    {
                        await connection.CloseAsync(
                            1,
                            ct);

                        return;
                    }

                    if(!ready.TryGetProperty(
                        "token",
                        out var tokenElement))
                    {
                        await connection.CloseAsync(
                            1,
                            ct);

                        return;
                    }

                    var login =
                        tokenElement
                            .GetString()
                            ?.Trim();

                    if(string.IsNullOrWhiteSpace(login) ||
                       login.Length > 512)
                    {
                        await connection.CloseAsync(
                            1,
                            ct);

                        return;
                    }

                    var (wallet, worker) =
                        ParseLogin(login);

                    if(!QuantusEncoding.IsAddress(wallet))
                        throw new FormatException("Invalid Quantus payout address");

                    var minerId =
                        Interlocked.Increment(
                            ref nextMinerId);

                    var remoteIpAddress =
                        connection.RemoteEndPoint is IPEndPoint remoteEndPoint
                            ? remoteEndPoint.Address.ToString()
                            : "unknown";

                    session =
                        new MinerSession(
                            minerId,
                            login,
                            wallet,
                            worker,
                            remoteIpAddress,
                            stream, lifetime);

                    sessions[minerId] =
                        session;

                    try
                    {
                        MinerConnected?.Invoke(
                            new QuantusPoolMiner(
                                session.Id,
                                session.Login,
                                session.Wallet,
                                session.Worker,
                                session.IpAddress));
                    }
                    catch
                    {
                        // Telemetry must never terminate a miner connection.
                    }
                }

                if(GetCurrentJob() != null)
                    await IssueJobAsync(
                        session,
                        ct);

                while(!ct.IsCancellationRequested)
                {
                    var payload =
                        await ReadMessageAsync(
                            stream,
                            ct);

                    if(!session.AllowResult())
                        throw new InvalidDataException("Quantus message rate exceeded");

                    using var doc =
                        JsonDocument.Parse(payload);

                    if(!doc.RootElement
                        .TryGetProperty(
                            "JobResult",
                            out var element))
                    {
                        continue;
                    }

                    var result =
                        element.Deserialize<
                            QuantusMiningResult>();

                    if(result == null)
                        continue;

                    await HandleResultAsync(
                        session,
                        result,
                        ct);
                }
            }
            catch(OperationCanceledException)
                when(ct.IsCancellationRequested)
            {
            }
            catch
            {
                // Connection terminated or malformed client.
            }
            finally
            {
                if(session != null)
                {
                    sessions.TryRemove(
                        session.Id,
                        out _);

                    try
                    {
                        MinerDisconnected?.Invoke(
                            new QuantusPoolMiner(
                                session.Id,
                                session.Login,
                                session.Wallet,
                                session.Worker,
                                session.IpAddress));
                    }
                    catch
                    {
                        // Telemetry must never affect connection cleanup.
                    }
                }

                if(stream != null)
                    await stream.DisposeAsync();
            }
        }
    }

    private async Task HandleResultAsync(
        MinerSession miner,
        QuantusMiningResult submission,
        CancellationToken ct)
    {
        QuantusJob assignedJob;
        BigInteger assignedDifficulty;
        lock(miner.StateLock)
        {
            if(submission.JobId == null || !miner.Assignments.Remove(submission.JobId, out var assignment))
                return; // Includes cancellations for old work. Never replace the current job here.
            assignedJob = assignment.Job;
            assignedDifficulty = assignment.Difficulty;
        }
        if(!ReferenceEquals(assignedJob, GetCurrentJob())) return;
        if(string.Equals(submission.Status, "cancelled", StringComparison.OrdinalIgnoreCase)) return;

        QuantusShareResult validation;

        if(!string.Equals(
            submission.Status,
            "completed",
            StringComparison.OrdinalIgnoreCase))
        {
            validation =
                QuantusShareResult.Rejected(
                    $"miner returned status {submission.Status}");
        }
        else if(string.IsNullOrWhiteSpace(
            submission.Work))
        {
            validation =
                QuantusShareResult.Rejected(
                    "miner returned no work");
        }
        else
        {
            validation =
                assignedJob.ProcessShare(
                    submission.Work,
                    assignedDifficulty);
        }

        var share =
            new QuantusPoolShare(
                miner.Id,
                miner.Login,
                miner.Wallet,
                miner.Worker,
                miner.IpAddress,
                assignedJob,
                assignedDifficulty,
                submission,
                validation);

        var handler =
            ShareReceived;

        if(handler != null)
            await handler(share);

        // Quantus miner stops after its first result.
        // Give it a fresh downstream job so it resumes.
        await IssueJobAsync(miner, ct, submission.JobId);
    }

    private async Task IssueJobAsync(
        MinerSession miner,
        CancellationToken ct, string completedJobId = null)
    {
        using var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct, miner.Lifetime.Token);
        sendTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        ct = sendTimeout.Token;
        await miner.SendLock.WaitAsync(ct);

        try
        {
            lock(miner.StateLock)
            {
                if(completedJobId != null && miner.AssignedJobId != completedJobId) return;
            }
            var job =
                GetCurrentJob();

            if(job == null)
                return;

            var seq =
                Interlocked.Increment(
                    ref nextDownstreamJobId);

            var downstreamId =
                $"{job.UpstreamJobId}-{seq:x}";

            BigInteger difficulty;

            lock(miner.StateLock)
            {
                difficulty =
                    miner.CurrentDifficulty >
                    BigInteger.Zero
                        ? miner.CurrentDifficulty
                        : poolDifficulty;

                difficulty = BigInteger.Min(difficulty, job.NetworkDifficulty);
                if(!ReferenceEquals(miner.AssignedJob, job)) miner.Assignments.Clear();
                while(miner.Assignments.Count >= 4) miner.Assignments.Remove(miner.Assignments.Keys.First());
                miner.Assignments[downstreamId] = (job, difficulty);

                miner.AssignedJob =
                    job;

                miner.AssignedJobId =
                    downstreamId;

                miner.AssignedDifficulty =
                    difficulty;
            }

            var request =
                new QuantusMiningRequest
                {
                    JobId =
                        downstreamId,

                    MiningHash =
                        job.MiningHash,

                    Difficulty =
                        difficulty.ToString(
                            CultureInfo.InvariantCulture)
                };

            await WriteMessageAsync(
                miner.Stream,
                new
                {
                    NewJob = request
                },
                ct);
        }
        finally
        {
            miner.SendLock.Release();
        }
    }

    private QuantusJob GetCurrentJob()
    {
        lock(jobLock)
            return currentJob;
    }

    private static (
        string wallet,
        string worker)
        ParseLogin(string login)
    {
        var separator =
            login.IndexOf('.');

        if(separator <= 0)
            return (
                login,
                "default");

        var wallet =
            login[..separator];

        var worker =
            separator + 1 < login.Length
                ? login[(separator + 1)..]
                : "default";

        if(string.IsNullOrWhiteSpace(worker))
            worker = "default";

        return (
            wallet,
            worker);
    }

    private static async Task<byte[]>
        ReadMessageAsync(
            QuicStream stream,
            CancellationToken ct)
    {
        var size = new byte[4];

        await stream.ReadExactlyAsync(
            size,
            ct);

        var length =
            BinaryPrimitives
                .ReadUInt32BigEndian(size);

        if(length == 0 ||
           length >
           QuantusConstants.MaxMessageSize)
        {
            throw new InvalidDataException(
                $"Invalid Quantus message size: {length}");
        }

        var payload =
            new byte[length];

        await stream.ReadExactlyAsync(
            payload,
            ct);

        return payload;
    }

    private static async Task
        WriteMessageAsync(
            QuicStream stream,
            object message,
            CancellationToken ct)
    {
        var payload =
            JsonSerializer
                .SerializeToUtf8Bytes(
                    message);

        if(payload.Length >
           QuantusConstants.MaxMessageSize)
        {
            throw new InvalidDataException(
                "Quantus message too large");
        }

        var frame =
            new byte[
                payload.Length + 4];

        BinaryPrimitives
            .WriteUInt32BigEndian(
                frame.AsSpan(0, 4),
                (uint) payload.Length);

        payload.CopyTo(
            frame.AsSpan(4));

        await stream.WriteAsync(
            frame,
            ct);

        await stream.FlushAsync(
            ct);
    }

    public async ValueTask DisposeAsync()
    {
        if(cts != null)
        {
            cts.Cancel();

            try
            {
                if(acceptTask != null)
                    await acceptTask;
            }
            catch
            {
            }
        }

        foreach(var miner in
            sessions.Values)
        {
            try
            {
                await miner.Stream
                    .DisposeAsync();
            }
            catch
            {
            }
        }

        sessions.Clear();

        if(listener != null)
            await listener.DisposeAsync();

        cts?.Dispose();

        certificate.Dispose();
    }

    private sealed class MinerSession
    {
        public MinerSession(
            long id,
            string login,
            string wallet,
            string worker,
            string ipAddress,
            QuicStream stream, CancellationTokenSource lifetime)
        {
            Lifetime = lifetime;
            Id = id;
            Login = login;
            Wallet = wallet;
            Worker = worker;
            IpAddress = ipAddress;
            Stream = stream;
        }

        public long Id { get; }

        public string Login { get; }

        public string Wallet { get; }

        public string Worker { get; }

        public string IpAddress { get; }

        public QuicStream Stream { get; }
        public CancellationTokenSource Lifetime { get; }
        public void Stop() { try { Lifetime.Cancel(); } catch(ObjectDisposedException) { } }
        public Dictionary<string, (QuantusJob Job, BigInteger Difficulty)> Assignments { get; } = new();
        private long rateWindow = Environment.TickCount64;
        private int resultCount;
        public bool AllowResult()
        {
            var now = Environment.TickCount64;
            if(now - rateWindow >= 1000) { rateWindow = now; resultCount = 0; }
            return ++resultCount <= 50;
        }

        public object StateLock { get; } =
            new();

        public SemaphoreSlim SendLock { get; } =
            new(1, 1);

        public QuantusJob AssignedJob { get; set; }

        public string AssignedJobId { get; set; }

        public BigInteger CurrentDifficulty { get; set; }

        public BigInteger AssignedDifficulty { get; set; }
    }
}

public sealed record QuantusPoolMiner(
    long MinerId,
    string Login,
    string Wallet,
    string Worker,
    string IpAddress);

public sealed record QuantusPoolShare(
    long MinerId,
    string Login,
    string Wallet,
    string Worker,
    string IpAddress,
    QuantusJob Job,
    BigInteger AssignedDifficulty,
    QuantusMiningResult Submission,
    QuantusShareResult Validation);
