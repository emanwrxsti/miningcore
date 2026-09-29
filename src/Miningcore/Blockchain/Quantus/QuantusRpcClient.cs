using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Miningcore.Blockchain.Quantus;

public sealed class QuantusRpcClient : IDisposable
{
    private readonly HttpClient http;
    private long nextId;

    public QuantusRpcClient(string endpoint)
    {
        if(string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException(
                "Quantus RPC endpoint is required",
                nameof(endpoint));

        http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
            BaseAddress = new Uri(endpoint)
        };
    }

    public async Task<long> GetTipHeightAsync(
        CancellationToken ct = default)
    {
        var result =
            await CallAsync(
                "chain_getHeader",
                [],
                ct);

        if(result.ValueKind != JsonValueKind.Object ||
           !result.TryGetProperty("number", out var numberElement))
        {
            throw new InvalidDataException(
                "Quantus chain_getHeader returned no block number");
        }

        var value =
            numberElement.GetString();

        if(string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException(
                "Quantus header block number is empty");

        if(value.StartsWith(
            "0x",
            StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if(!long.TryParse(
            value,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out var height))
        {
            throw new InvalidDataException(
                $"Invalid Quantus block height: {value}");
        }

        return height;
    }

    public async Task<string> GetBlockHashAsync(long height, CancellationToken ct = default)
    {
        var result = await CallAsync("chain_getBlockHash", [height], ct);
        return result.ValueKind == JsonValueKind.Null ? null : "0x" + QuantusEncoding.Hex(result.GetString(), 32);
    }

    public async Task<QuantusHeader> GetHeaderAsync(string hash, CancellationToken ct = default)
    {
        var result = await CallAsync("chain_getHeader", [hash], ct);
        if(result.ValueKind == JsonValueKind.Null) return null;
        var header = QuantusHeader.Parse(result);
        if(!string.Equals(header.Hash, "0x" + QuantusEncoding.Hex(hash, 32), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Quantus header hash disagrees with RPC: unsupported runtime or invalid data");
        return header;
    }

    public async Task<QuantusHeader> GetFinalizedHeaderAsync(CancellationToken ct = default)
    {
        var hash = (await CallAsync("chain_getFinalizedHead", [], ct)).GetString();
        return await GetHeaderAsync(hash, ct) ?? throw new InvalidDataException("Finalized header unavailable");
    }

    public async Task<QuantusNodeHealth> GetHealthAsync(
        CancellationToken ct = default)
    {
        var result =
            await CallAsync(
                "system_health",
                [],
                ct);

        return result.Deserialize<QuantusNodeHealth>()
            ?? throw new InvalidDataException(
                "Unable to deserialize Quantus system_health");
    }

    public async Task<string> GetVersionAsync(
        CancellationToken ct = default)
    {
        var result =
            await CallAsync(
                "system_version",
                [],
                ct);

        return result.GetString()
            ?? throw new InvalidDataException(
                "Quantus system_version returned no value");
    }

    private async Task<JsonElement> CallAsync(
        string method,
        object[] parameters,
        CancellationToken ct)
    {
        var id =
            Interlocked.Increment(
                ref nextId);

        var request =
            new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters
            };

        var json =
            JsonSerializer.Serialize(request);

        using var content =
            new StringContent(
                json,
                Encoding.UTF8,
                "application/json");

        using var response =
            await http.PostAsync(
                string.Empty,
                content,
                ct);

        response.EnsureSuccessStatusCode();

        await using var stream =
            await response.Content
                .ReadAsStreamAsync(ct);

        using var document =
            await JsonDocument.ParseAsync(
                stream,
                cancellationToken: ct);

        var root =
            document.RootElement;

        if(root.TryGetProperty(
            "error",
            out var error) &&
           error.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"Quantus RPC {method} failed: {error}");
        }

        if(!root.TryGetProperty(
            "result",
            out var result))
        {
            throw new InvalidDataException(
                $"Quantus RPC {method} returned no result");
        }

        return result.Clone();
    }

    public void Dispose()
    {
        http.Dispose();
    }
}

public sealed class QuantusNodeHealth
{
    [JsonPropertyName("peers")]
    public int Peers { get; set; }

    [JsonPropertyName("isSyncing")]
    public bool IsSyncing { get; set; }

    [JsonPropertyName("shouldHavePeers")]
    public bool ShouldHavePeers { get; set; }
}
