using System.Text.Json.Serialization;

namespace Miningcore.Blockchain.Quantus;

public static class QuantusConstants
{
    public const int DefaultMinerPort = 9833;
    public const int MaxMessageSize = 1024;
    public const string MinerAlpn = "quantus-miner/2";
}

public class QuantusMiningRequest
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; }

    [JsonPropertyName("mining_hash")]
    public string MiningHash { get; set; }

    [JsonPropertyName("difficulty")]
    public string Difficulty { get; set; }
}

public class QuantusMiningResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("job_id")]
    public string JobId { get; set; }

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; }

    [JsonPropertyName("work")]
    public string Work { get; set; }

    [JsonPropertyName("hash_count")]
    public ulong HashCount { get; set; }

    [JsonPropertyName("elapsed_time")]
    public double ElapsedTime { get; set; }
}
