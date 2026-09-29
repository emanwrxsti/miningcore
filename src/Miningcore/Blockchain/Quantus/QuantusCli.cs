using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Miningcore.Persistence.Model;

namespace Miningcore.Blockchain.Quantus;

// Adapter for quantus-cli b29fb45b1463f0e850570ab9d573174a8cd418c6.
// CLI text is an API here: upgrades require an explicit binary pin and review.
public sealed class QuantusCli
{
    public const decimal SmallestUnit = 1_000_000_000_000m;
    private readonly QuantusPaymentProcessingConfigExtra config;
    private readonly string nodeUrl;
    private static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex Finalized = new(@"Batch transaction finalized\. Hash: (0x[0-9a-fA-F]{64})(?![0-9a-fA-F])", RegexOptions.Compiled);

    public QuantusCli(QuantusPaymentProcessingConfigExtra config, string nodeUrl)
    { this.config = config; this.nodeUrl = nodeUrl; }

    public async Task VerifyBinaryAsync(CancellationToken ct)
    {
        var expected = QuantusEncoding.Hex(config.CliSha256, 32);
        await using var file = File.OpenRead(config.CliPath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
        if(actual != expected)
            throw new InvalidOperationException("Quantus CLI SHA-256 does not match cliSha256; refusing to execute it");
    }

    public async Task<string> QueryEventsAsync(string hash, CancellationToken ct)
    {
        var result = await RunAsync(["events", "--block-hash", "0x" + QuantusEncoding.Hex(hash, 32), "--pallet", "MiningRewards"], TimeSpan.FromSeconds(60), ct);
        result.RequireSuccess();
        return result.Output;
    }

    public async Task VerifyWalletAsync(string address, CancellationToken ct)
    {
        var result = await RunAsync(["wallet", "view", "--name", config.WalletName], TimeSpan.FromSeconds(60), ct);
        result.RequireSuccess();
        var matches = Regex.Matches(result.Output, @"(?m)^Address: ([1-9A-HJ-NP-Za-km-z]+)\s*$");
        if(matches.Count != 1 || matches[0].Groups[1].Value != address)
            throw new InvalidOperationException("Quantus wallet public address does not match pool address");
    }

    public async Task CollectRewardsAsync(string destination, uint finalizedHeight, CancellationToken ct)
    {
        var args = new List<string> { "wormhole", "collect-rewards", "--destination", destination,
            "--subsquid-url", config.SubsquidUrl, "--at-block", finalizedHeight.ToString(CultureInfo.InvariantCulture) };
        if(!string.IsNullOrWhiteSpace(config.WormholeSecretFile)) args.AddRange(["--secret-file", config.WormholeSecretFile]);
        else args.AddRange(["--wallet", config.WalletName, "--password-file", config.PasswordFile,
            "--wormhole-index", config.WormholeIndex.ToString(CultureInfo.InvariantCulture)]);
        var preview = await RunAsync(args.Append("--dry-run"), TimeSpan.FromSeconds(config.CliTimeoutSeconds), ct);
        preview.RequireSuccess();
        var addresses = Regex.Matches(preview.Output, @"Derived wormhole address: ([1-9A-HJ-NP-Za-km-z]+)");
        if(addresses.Count != 1 || addresses[0].Groups[1].Value != config.WormholeAddress)
            throw new InvalidOperationException("Collection credential does not derive the configured wormhole address");
        // The pinned CLI verifies on-chain nullifiers. Repeated collection cannot spend a reward twice.
        (await RunAsync(args, TimeSpan.FromSeconds(config.CliTimeoutSeconds), ct)).RequireSuccess();
    }

    public async Task<string> SendBatchAsync(Balance[] balances, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "quantus-batch-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if(!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using(var file = new FileStream(path, options))
                await JsonSerializer.SerializeAsync(file, BatchTransfers(balances), cancellationToken: ct);
            var result = await RunAsync(["batch", "send", "--from", config.WalletName, "--password-file", config.PasswordFile,
                "--batch-file", path, "--finalized-tx"], TimeSpan.FromSeconds(config.CliTimeoutSeconds), ct);
            // Dispatch success and finality precede this receipt. A later balance query may fail.
            return ParseFinalizedHash(result.Output);
        }
        finally { File.Delete(path); }
    }

    internal static object[] BatchTransfers(Balance[] balances) => balances.Select(balance =>
    {
        if(!QuantusEncoding.IsAddress(balance.Address)) throw new FormatException("Invalid Quantus payout address");
        var raw = checked(balance.Amount * SmallestUnit);
        if(raw <= 0 || raw != decimal.Truncate(raw)) throw new FormatException("Payout must be positive with at most 12 decimal places");
        return (object)new { to = balance.Address, amount = raw.ToString("0", CultureInfo.InvariantCulture) };
    }).ToArray();

    internal static string ParseFinalizedHash(string output)
    {
        var matches = Finalized.Matches(Ansi.Replace(output, ""));
        if(matches.Count != 1)
            throw new InvalidOperationException("Quantus send outcome is uncertain: no unique finalized receipt; reconcile the payout journal before resuming");
        return "0x" + QuantusEncoding.Hex(matches[0].Groups[1].Value, 32);
    }

    private async Task<CliResult> RunAsync(IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        await VerifyBinaryAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var psi = new ProcessStartInfo(config.CliPath) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach(var arg in arguments) psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("--node-url"); psi.ArgumentList.Add(nodeUrl);
        psi.Environment["NO_COLOR"] = "1"; psi.Environment["CLICOLOR"] = "0";
        using var process = new Process { StartInfo = psi };
        if(!process.Start()) throw new InvalidOperationException("Cannot start Quantus CLI");
        process.StandardInput.Close();
        var stdout = ReadBoundedAsync(process.StandardOutput, deadline);
        var stderr = ReadBoundedAsync(process.StandardError, deadline);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return new CliResult(process.ExitCode, Ansi.Replace((await stdout) + "\n" + (await stderr), ""));
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch(InvalidOperationException) { }
            deadline.Cancel();
            try { await Task.WhenAll(stdout, stderr); } catch { }
            // Never log CLI output: upstream errors can contain wallet data.
            throw new InvalidOperationException("Quantus CLI did not finish within its limits; any submitted payout requires journal reconciliation");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationTokenSource deadline)
    {
        var output = new StringBuilder(); var buffer = new char[4096]; int count;
        while((count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token)) != 0)
        {
            if(output.Length + count > 1_048_576) { deadline.Cancel(); throw new InvalidDataException("CLI output limit exceeded"); }
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }

    private sealed record CliResult(int ExitCode, string Output)
    {
        public void RequireSuccess()
        {
            if(ExitCode != 0) throw new InvalidOperationException($"Quantus CLI exited with code {ExitCode}; check it independently under the service account");
        }
    }
}
