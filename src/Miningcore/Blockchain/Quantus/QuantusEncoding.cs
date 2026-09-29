using System.Security.Cryptography;
using System.Text;
using NBitcoin.DataEncoders;
using Org.BouncyCastle.Crypto.Digests;

namespace Miningcore.Blockchain.Quantus;

public static class QuantusEncoding
{
    public static string Hex(string value, int bytes)
    {
        if(string.IsNullOrWhiteSpace(value)) throw new FormatException("Missing hex value");
        value = value.Trim();
        if(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        if(value.Length != bytes * 2) throw new FormatException($"Expected {bytes} bytes");
        return Convert.ToHexString(Convert.FromHexString(value)).ToLowerInvariant();
    }

    // SS58 AccountId32, network 189. Prefix bytes are the canonical two-byte SS58 encoding.
    public static bool IsAddress(string address)
    {
        try
        {
            if(address == null || address.Length < 45 || address.Length > 55) return false;
            var data = Encoders.Base58.DecodeData(address);
            if(data.Length != 36 || data[0] != 0x6f || data[1] != 0x40) return false;
            var input = Encoding.ASCII.GetBytes("SS58PRE").Concat(data.Take(34)).ToArray();
            var digest = new Blake2bDigest(512);
            digest.BlockUpdate(input, 0, input.Length);
            var hash = new byte[64];
            digest.DoFinal(hash, 0);
            return CryptographicOperations.FixedTimeEquals(data.AsSpan(34, 2), hash.AsSpan(0, 2)) &&
                Encoders.Base58.EncodeData(data) == address;
        }
        catch(FormatException) { return false; }
    }
}
