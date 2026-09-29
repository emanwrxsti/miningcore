using System.Buffers.Binary;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace Miningcore.Blockchain.Quantus;

public sealed class QuantusQuicClient : IAsyncDisposable
{
    private readonly string host;
    private readonly int port;
    private readonly string authTokenPath;
    private readonly string tlsFingerprintPath;

    private readonly SemaphoreSlim sendLock = new(1, 1);

    private QuicConnection connection;
    private QuicStream stream;
    private CancellationTokenSource cts;
    private Task receiveTask;

    public QuantusQuicClient(
        string host,
        int port,
        string authTokenPath,
        string tlsFingerprintPath)
    {
        this.host = host;
        this.port = port;
        this.authTokenPath = authTokenPath;
        this.tlsFingerprintPath = tlsFingerprintPath;
    }

    public event Func<QuantusMiningRequest, Task> JobReceived;

    public bool IsConnected =>
        connection != null &&
        stream != null && receiveTask is { IsCompleted: false };

    public Task Completion => receiveTask ?? Task.CompletedTask;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if(!QuicConnection.IsSupported)
            throw new PlatformNotSupportedException(
                "System.Net.Quic is not supported. Ensure libmsquic is installed.");

        var token = File.ReadAllText(authTokenPath).Trim();

        if(string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Quantus miner auth token is empty");

        var expectedFingerprint = File.ReadAllText(tlsFingerprintPath)
            .Trim()
            .Replace(":", "")
            .ToLowerInvariant();

        if(expectedFingerprint.Length != 64)
            throw new InvalidOperationException(
                "Quantus TLS SHA-256 fingerprint must contain 64 hex characters");

        var expectedFingerprintBytes = Convert.FromHexString(expectedFingerprint);

        IPAddress address;

        if(!IPAddress.TryParse(host, out address))
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);

            address = addresses
                .FirstOrDefault(x => x.AddressFamily ==
                    System.Net.Sockets.AddressFamily.InterNetwork)
                ?? addresses.First();
        }

        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(address, port),

            IdleTimeout = TimeSpan.FromSeconds(60),
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,

            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols =
                [
                    new SslApplicationProtocol(QuantusConstants.MinerAlpn)
                ],

                RemoteCertificateValidationCallback =
                    (_, cert, _, _) =>
                    {
                        if(cert == null)
                            return false;

                        var fingerprint =
                            Convert.ToHexString(
                                SHA256.HashData(cert.GetRawCertData()))
                            .ToLowerInvariant();

                        return CryptographicOperations.FixedTimeEquals(
                            Convert.FromHexString(fingerprint),
                            expectedFingerprintBytes);
                    }
            }
        };

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        var connectToken = connectTimeout.Token;
        connection = await QuicConnection.ConnectAsync(options, connectToken);

        stream = await connection.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional,
            connectToken);

        await WriteMessageAsync(
            new
            {
                Ready = new
                {
                    token
                }
            },
            connectToken);

        cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        receiveTask = ReceiveLoopAsync(cts.Token);
    }

    public async Task SubmitResultAsync(
        QuantusMiningResult result,
        CancellationToken ct = default)
    {
        if(stream == null)
            throw new InvalidOperationException(
                "Quantus QUIC connection is not established");

        await WriteMessageAsync(
            new
            {
                JobResult = result
            },
            ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            byte[] payload;

            try
            {
                payload = await ReadMessageAsync(ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested)
            {
                break;
            }

            using var doc = JsonDocument.Parse(payload);

            if(!doc.RootElement.TryGetProperty("NewJob", out var newJob))
                continue;

            var job = newJob.Deserialize<QuantusMiningRequest>();

            if(job == null)
                continue;

            var handler = JobReceived;

            if(handler != null)
                await handler(job);
        }
    }

    private async Task<byte[]> ReadMessageAsync(CancellationToken ct)
    {
        var lengthBuffer = new byte[4];

        await stream.ReadExactlyAsync(lengthBuffer, ct);

        var length =
            BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);

        if(length == 0 || length > QuantusConstants.MaxMessageSize)
            throw new InvalidDataException(
                $"Invalid Quantus message size: {length}");

        var payload = new byte[length];

        await stream.ReadExactlyAsync(payload, ct);

        return payload;
    }

    private async Task WriteMessageAsync(
        object message,
        CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);

        if(payload.Length > QuantusConstants.MaxMessageSize)
            throw new InvalidDataException(
                $"Quantus message exceeds {QuantusConstants.MaxMessageSize} bytes");

        var frame = new byte[4 + payload.Length];

        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(0, 4),
            (uint)payload.Length);

        payload.CopyTo(frame.AsSpan(4));

        await sendLock.WaitAsync(ct);

        try
        {
            await stream.WriteAsync(frame, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if(cts != null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        if(stream != null)
            await stream.DisposeAsync();

        if(connection != null)
            await connection.DisposeAsync();

        if(receiveTask != null)
        {
            try { await receiveTask; } catch { /* Supervisor observes the original failure. */ }
        }
        stream = null;
        connection = null;

        // Pending submissions may still release this managed semaphore; leave it for GC.
    }
}
