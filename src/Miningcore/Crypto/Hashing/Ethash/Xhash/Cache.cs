using System.Diagnostics;
using Miningcore.Blockchain.Ethereum;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Native;
using Miningcore.Notifications.Messages;
using NLog;

namespace Miningcore.Crypto.Hashing.Ethash.Xhash;

[Identifier("xhash")]
public class Cache : IEthashCache
{
    public Cache(ulong epoch, string dagDir = null)
    {
        Epoch = epoch;
        this.dagDir = dagDir;
        LastUsed = DateTime.Now;
    }

    private IntPtr handle = IntPtr.Zero;
    private bool isGenerated = false;
    private readonly object genLock = new();
    internal static IMessageBus messageBus;
    public ulong Epoch { get; }
    private string dagDir;
    public DateTime LastUsed { get; set; }

    public void Dispose()
    {
        if(handle != IntPtr.Zero)
        {
            XHash.xhash_destroy_epoch_context(handle);
            handle = IntPtr.Zero;
        }
    }

    public async Task GenerateAsync(ILogger logger, CancellationToken ct)
    {
        if(handle == IntPtr.Zero)
        {
            await Task.Run(() =>
            {
                lock(genLock)
                {
                    if(!isGenerated)
                    {
                        // re-check after obtaining lock
                        if(handle != IntPtr.Zero)
                            return;
                        
                        var started = DateTime.Now;
                        var epochNum = (int)Epoch;

                        logger.Debug(() => $"Generating cache for epoch {Epoch}");
                        logger.Debug(() => $"Epoch length used: {XHashConstants.EpochLength}");

                        handle = XHash.xhash_create_epoch_context(epochNum);

                        if(handle == IntPtr.Zero)
                            throw new OutOfMemoryException("xhash_create_epoch_context memory error");

                        logger.Debug(() => $"Done generating cache for epoch {Epoch} after {DateTime.Now - started}");
                        isGenerated = true;
                    }
                }
            }, ct);
        }
    }

    public unsafe bool Compute(ILogger logger, byte[] hash, ulong nonce, out byte[] mixDigest, out byte[] result)
    {
        Contract.RequiresNonNull(hash);

        var sw = Stopwatch.StartNew();

        mixDigest = null;
        result = null;

        fixed (byte* input = hash)
        {
            var headerHash = new XHash.xhash_hash256();
            headerHash.bytes = new byte[32];
            System.Buffer.BlockCopy(hash, 0, headerHash.bytes, 0, 32);

            var xhashResult = XHash.xhash_hash(handle, ref headerHash, nonce);

            mixDigest = xhashResult.mix_hash.bytes;
            result = xhashResult.final_hash.bytes;
        }

        messageBus?.SendTelemetry("XHash", TelemetryCategory.Hash, sw.Elapsed, true);

        return true;
    }
}

