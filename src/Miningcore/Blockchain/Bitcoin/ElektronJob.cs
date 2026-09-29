using Miningcore.Extensions;
using Miningcore.Stratum;

namespace Miningcore.Blockchain.Bitcoin;

/// <summary>
/// Elektron Net SHA256d job.
///
/// Elektron uses Bitcoin-style headers and SHA256d, but its coinbase is
/// consensus-sensitive because the node commits to a UTXO attestation.
///
/// Rules:
/// - tx version = 2
/// - scriptSig = coinbase_script_sig_prefix exactly
/// - no extranonce1/extranonce2 inserted into the coinbase
/// - nSequence = 0xfffffffe
/// - vout[0] = full block reward to pool wallet
/// - vout[1..] = coinbase_required_outputs verbatim
/// - nLockTime = height - 1
/// - coinbase witness = one 32-byte zero item
/// </summary>
public class ElektronJob : BitcoinJob
{
    private byte[] canonicalCoinbase;
    private byte[] witnessCoinbase;

    protected override void BuildCoinbase()
    {
        var scriptSigHex = BlockTemplate.CoinbaseScriptSigPrefix?.Trim();

        if(string.IsNullOrEmpty(scriptSigHex))
            throw new InvalidOperationException(
                "[ELEK] getblocktemplate did not return coinbase_script_sig_prefix");

        var scriptSig = scriptSigHex.HexToByteArray();

        var requiredOutputs = BlockTemplate.CoinbaseRequiredOutputs;

        if(requiredOutputs == null || requiredOutputs.Length == 0)
            throw new InvalidOperationException(
                "[ELEK] getblocktemplate did not return coinbase_required_outputs");

        var payoutScript = poolAddressDestination.ScriptPubKey.ToBytes(true);

        if(payoutScript.Length == 0)
            throw new InvalidOperationException(
                "[ELEK] Pool payout script is empty");

        var outputs = new List<(long Value, byte[] Script)>
        {
            // Elektron attestation expects the complete coinbase reward
            // to go to one payout output. Pool fees are handled later by
            // Miningcore accounting, not by another coinbase output.
            (BlockTemplate.CoinbaseValue, payoutScript)
        };

        foreach(var requiredOutput in requiredOutputs)
        {
            if(string.IsNullOrWhiteSpace(requiredOutput.ScriptPubKey))
                throw new InvalidOperationException(
                    "[ELEK] Required coinbase output has an empty scriptPubKey");

            outputs.Add((
                requiredOutput.Value,
                requiredOutput.ScriptPubKey.HexToByteArray()
            ));
        }

        //
        // Non-witness serialization.
        //
        // This exact byte stream is used for:
        // - txid
        // - merkle root
        // - Stratum coinb1 + coinb2
        //

        using(var stream = new MemoryStream())
        using(var writer = new BinaryWriter(stream))
        {
            // tx version 2
            writer.Write(2u);

            // one input
            WriteCompactSize(writer, 1);

            // null prevout
            writer.Write(new byte[32]);
            writer.Write(uint.MaxValue);

            // exact scriptSig supplied by Elektron
            WriteCompactSize(writer, (ulong) scriptSig.Length);
            writer.Write(scriptSig);

            // coinb1 ends exactly after scriptSig
            coinbaseInitial = stream.ToArray();
            coinbaseInitialHex = coinbaseInitial.ToHexString();
        }

        using(var stream = new MemoryStream())
        using(var writer = new BinaryWriter(stream))
        {
            // nSequence
            writer.Write(0xfffffffeu);

            // outputs
            WriteCompactSize(writer, (ulong) outputs.Count);

            foreach(var output in outputs)
            {
                writer.Write(output.Value);

                WriteCompactSize(writer, (ulong) output.Script.Length);
                writer.Write(output.Script);
            }

            // Elektron consensus
            writer.Write(BlockTemplate.Height - 1);

            coinbaseFinal = stream.ToArray();
            coinbaseFinalHex = coinbaseFinal.ToHexString();
        }

        canonicalCoinbase = new byte[
            coinbaseInitial.Length +
            coinbaseFinal.Length];

        Buffer.BlockCopy(
            coinbaseInitial,
            0,
            canonicalCoinbase,
            0,
            coinbaseInitial.Length);

        Buffer.BlockCopy(
            coinbaseFinal,
            0,
            canonicalCoinbase,
            coinbaseInitial.Length,
            coinbaseFinal.Length);

        //
        // Full witness serialization for submitblock.
        //

        using(var stream = new MemoryStream())
        using(var writer = new BinaryWriter(stream))
        {
            // tx version
            writer.Write(2u);

            // SegWit marker + flag
            writer.Write((byte) 0x00);
            writer.Write((byte) 0x01);

            // one input
            WriteCompactSize(writer, 1);

            // null prevout
            writer.Write(new byte[32]);
            writer.Write(uint.MaxValue);

            // exact scriptSig
            WriteCompactSize(writer, (ulong) scriptSig.Length);
            writer.Write(scriptSig);

            // nSequence
            writer.Write(0xfffffffeu);

            // outputs
            WriteCompactSize(writer, (ulong) outputs.Count);

            foreach(var output in outputs)
            {
                writer.Write(output.Value);

                WriteCompactSize(writer, (ulong) output.Script.Length);
                writer.Write(output.Script);
            }

            // witness stack:
            // one item containing exactly 32 zero bytes
            WriteCompactSize(writer, 1);
            WriteCompactSize(writer, 32);
            writer.Write(new byte[32]);

            // nLockTime
            writer.Write(BlockTemplate.Height - 1);

            witnessCoinbase = stream.ToArray();
        }
    }

    protected override byte[] SerializeCoinbase(
        string extraNonce1,
        string extraNonce2)
    {
        // Critical Elektron rule:
        //
        // extranonce1 and extranonce2 are NOT coinbase material.
        // The exact GBT scriptSig must remain unchanged.
        return canonicalCoinbase;
    }

    protected override byte[] SerializeBlock(
        byte[] header,
        byte[] coinbase)
    {
        var rawTransactionBuffer = BuildRawTransactionBuffer();

        var transactionCount =
            (uint) BlockTemplate.Transactions.Length + 1;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(header);

        WriteCompactSize(writer, transactionCount);

        // submitblock requires the witness form of the coinbase
        writer.Write(witnessCoinbase);

        writer.Write(rawTransactionBuffer);

        return stream.ToArray();
    }

    // Bits 13-16 are reserved for pool-side work rotation.
    // The miner receives the remaining BIP320 mask.
    public const uint PoolVersionBitsMask = 0x0001e000;
    public const uint MinerVersionBitsMask =
        BitcoinConstants.VersionRollingPoolMask & ~PoolVersionBitsMask;

    /// <summary>
    /// Derive a deterministic pool-side version for this worker/job.
    ///
    /// Using connection-id + job-id means:
    /// - the same worker/job always receives the same version
    /// - vardiff re-announcements do not silently change work
    /// - different workers/jobs are distributed across the 16 reserved
    ///   Elektron pool-side BIP320 states
    /// </summary>
    public static uint GetWorkerVersion(
        uint templateVersion,
        string connectionId,
        string jobId)
    {
        unchecked
        {
            // FNV-1a, used only as a fast deterministic distributor.
            uint hash = 2166136261;

            var value = $"{connectionId}:{jobId}";

            foreach(var ch in value)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            var rotationState = hash & 0x0f;
            var poolBits = rotationState << 13;

            return
                (templateVersion & ~PoolVersionBitsMask) |
                (poolBits & PoolVersionBitsMask);
        }
    }

    protected override uint? GetBaseVersionForWorker(
        StratumConnection worker)
    {
        var context = worker.ContextAs<BitcoinWorkerContext>();

        // Local Elektron work stores the exact base version that was sent in
        // mining.notify. Use that value when validating a returned share.
        if(context.ElektronSubmitBaseVersion.HasValue)
            return context.ElektronSubmitBaseVersion.Value;

        // Compatibility fallback for older/non-local work assignments.
        return GetWorkerVersion(
            BlockTemplate.Version,
            worker.ConnectionId,
            JobId);
    }

    private static void WriteCompactSize(
        BinaryWriter writer,
        ulong value)
    {
        if(value < 253)
        {
            writer.Write((byte) value);
        }
        else if(value <= ushort.MaxValue)
        {
            writer.Write((byte) 253);
            writer.Write((ushort) value);
        }
        else if(value <= uint.MaxValue)
        {
            writer.Write((byte) 254);
            writer.Write((uint) value);
        }
        else
        {
            writer.Write((byte) 255);
            writer.Write(value);
        }
    }
}
