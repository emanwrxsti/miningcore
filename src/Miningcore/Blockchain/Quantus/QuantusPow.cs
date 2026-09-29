using System.Numerics;
using System.Runtime.InteropServices;

namespace Miningcore.Blockchain.Quantus;

public static class QuantusPow
{
    private const string NativeLibrary = "quantuspow";

    [DllImport(NativeLibrary, EntryPoint = "quantus_pow_abi_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint AbiVersion();

    [DllImport(NativeLibrary, EntryPoint = "quantus_header_hash", CallingConvention = CallingConvention.Cdecl)]
    private static extern int HeaderHash([In] byte[] fields, uint number, [In] byte[] digest, uint digestLength, [Out] byte[] output);

    public static byte[] ComputeHeaderHash(byte[] fields, uint number, byte[] digest)
    {
        if(fields.Length != 128 || digest.Length == 0 || digest.Length > 111)
            throw new ArgumentException("Invalid Quantus header fields or digest window");
        var output = new byte[32];
        if(HeaderHash(fields, number, digest, (uint)digest.Length, output) != 0)
            throw new InvalidOperationException("Quantus header hash failed");
        return output;
    }

    public static void EnsureAvailable()
    {
        if(AbiVersion() != 1)
            throw new InvalidOperationException("Unsupported quantuspow ABI; rebuild the bundled native library");
        var hash = ComputeHash(new byte[32], Enumerable.Repeat((byte)1, 64).ToArray());
        // Regression vector from pinned upstream core, checked by the Rust FFI tests.
        if(Convert.ToHexString(hash) != SelfTestHash)
            throw new InvalidOperationException("Quantus Poseidon known-answer self-test failed");
    }

    private const string SelfTestHash = "20F16094A3DD2D8A5CDD7D0B04932FF206752F5A5A9ECA790368C6A70977256AEF126A65838683A5BB5A7556157C783F283D163F98B752B587A462B35D839064";

    public static readonly BigInteger MaxTarget =
        (BigInteger.One << 512) - BigInteger.One;

    [DllImport(
        NativeLibrary,
        EntryPoint = "quantus_qpow_hash",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int QuantusQpowHash(
        [In] byte[] blockHash,
        [In] byte[] nonce,
        [Out] byte[] outputHash);

    public static byte[] ComputeHash(
        ReadOnlySpan<byte> blockHash,
        ReadOnlySpan<byte> nonce)
    {
        if(blockHash.Length != 32)
            throw new ArgumentException(
                "Quantus mining hash must be exactly 32 bytes",
                nameof(blockHash));

        if(nonce.Length != 64)
            throw new ArgumentException(
                "Quantus nonce must be exactly 64 bytes",
                nameof(nonce));

        var output = new byte[64];

        var rc = QuantusQpowHash(
            blockHash.ToArray(),
            nonce.ToArray(),
            output);

        if(rc != 0)
            throw new InvalidOperationException(
                $"quantus_qpow_hash failed with code {rc}");

        return output;
    }

    public static byte[] ComputeHash(
        string miningHashHex,
        string nonceHex)
    {
        var blockHash = Convert.FromHexString(
            NormalizeHex(miningHashHex));

        var nonce = Convert.FromHexString(
            NormalizeHex(nonceHex));

        return ComputeHash(blockHash, nonce);
    }

    public static BigInteger ComputeHashValue(
        string miningHashHex,
        string nonceHex)
    {
        var hash = ComputeHash(miningHashHex, nonceHex);

        return new BigInteger(
            hash,
            isUnsigned: true,
            isBigEndian: true);
    }

    public static bool MeetsDifficulty(
        string miningHashHex,
        string nonceHex,
        BigInteger difficulty,
        out BigInteger achievedDifficulty,
        out BigInteger hashValue)
    {
        if(difficulty <= BigInteger.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(difficulty),
                "Difficulty must be greater than zero");

        hashValue =
            ComputeHashValue(miningHashHex, nonceHex);

        var target = MaxTarget / difficulty;

        achievedDifficulty =
            hashValue.IsZero
                ? MaxTarget
                : MaxTarget / hashValue;

        // Quantus uses STRICTLY LESS THAN target.
        return hashValue < target;
    }

    private static string NormalizeHex(string value)
    {
        if(string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Hex value is empty");

        value = value.Trim();

        if(value.StartsWith("0x",
            StringComparison.OrdinalIgnoreCase))
            value = value[2..];

        return value;
    }
}
