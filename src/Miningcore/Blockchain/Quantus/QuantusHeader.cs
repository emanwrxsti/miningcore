using System.Globalization;
using System.Text.Json;

namespace Miningcore.Blockchain.Quantus;

public sealed record QuantusHeader(uint Number, byte[] Fields, byte[][] Logs)
{
    public static QuantusHeader Parse(JsonElement json)
    {
        var number = uint.Parse(json.GetProperty("number").GetString()![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var fields = new[] { "parentHash", "stateRoot", "extrinsicsRoot", "zkTreeRoot" }
            .SelectMany(name => Convert.FromHexString(QuantusEncoding.Hex(json.GetProperty(name).GetString(), 32))).ToArray();
        var logs = json.GetProperty("digest").GetProperty("logs").EnumerateArray()
            .Select(x => Convert.FromHexString(x.GetString()![2..])).ToArray();
        return new(number, fields, logs);
    }

    public string Hash => "0x" + Convert.ToHexString(QuantusPow.ComputeHeaderHash(Fields, Number, Digest(Logs))).ToLowerInvariant();

    public bool Matches(QuantusCandidate candidate) => MatchesMiningHash(candidate) &&
        Logs[^1].AsSpan(7).SequenceEqual(Convert.FromHexString(candidate.Work));

    public bool MatchesMiningHash(QuantusCandidate candidate)
    {
        var legacy = Logs.Length == 3 && Number <= 1_000_000 && Logs[1].AsSpan().SequenceEqual(new byte[] { 8 });
        if((Logs.Length != 2 && !legacy) || Logs[0].Length != 38 || Logs[^1].Length != 71) return false;
        if(Logs[0][0] != 6 || !Logs[0].AsSpan(1, 4).SequenceEqual("pow_"u8) || Logs[0][5] != 128) return false;
        var seal = Logs[^1];
        if(seal[0] != 5 || !seal.AsSpan(1, 4).SequenceEqual("pow_"u8) || seal[5] != 1 || seal[6] != 1) return false;
        var preHash = QuantusPow.ComputeHeaderHash(Fields, Number, Digest(Logs[..^1]));
        return preHash.AsSpan().SequenceEqual(Convert.FromHexString(candidate.MiningHash));
    }

    private static byte[] Digest(byte[][] logs)
    {
        if(logs.Length >= 64) throw new FormatException("Unexpected Quantus digest count");
        return new[]{(byte)(logs.Length << 2)}.Concat(logs.SelectMany(x=>x)).ToArray();
    }
}
