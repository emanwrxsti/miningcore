using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Miningcore.Blockchain.Quantus;

public sealed record QuantusCandidate(int Version, string MiningHash, string Work, long ObservedHeight)
{
    public string Id => "qpow:" + Convert.ToHexString(SHA256.HashData(
        Encoding.ASCII.GetBytes(MiningHash + Work))).ToLowerInvariant();

    public static QuantusCandidate Create(QuantusJob job, string work) =>
        new(1, QuantusEncoding.Hex(job.MiningHash, 32), QuantusEncoding.Hex(work, 64), job.Height);

    public string Serialize() => JsonSerializer.Serialize(this);

    public static QuantusCandidate Parse(string text)
    {
        var value = JsonSerializer.Deserialize<QuantusCandidate>(text ?? "null");
        if(value == null || value.Version != 1 || value.ObservedHeight < 1 || value.ObservedHeight > uint.MaxValue)
            throw new FormatException("Missing or unsupported Quantus candidate identity; legacy candidate requires manual reconciliation");
        return value with { MiningHash = QuantusEncoding.Hex(value.MiningHash, 32), Work = QuantusEncoding.Hex(value.Work, 64) };
    }
}
