using System.Globalization;
using System.Text.RegularExpressions;
using Miningcore.Persistence.Model;
namespace Miningcore.Blockchain.Quantus;

internal static class QuantusBlockClassifier
{
    private static readonly Regex Reward = new(
        @"MiningRewards\(MinerRewarded\s*\{\s*miner:\s*(?<miner>[1-9A-HJ-NP-Za-km-z]+),\s*reward:\s*(?<reward>[0-9]+)\s*\}\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static async Task ClassifyAsync(Block block, long tip, uint finalizedHeight, int confirmations,
        int searchWindow, string wormholeAddress, Func<long, CancellationToken, Task<QuantusHeader>> getHeader,
        Func<string, CancellationToken, Task<string>> getEvents, CancellationToken ct)
    {
        var candidate = QuantusCandidate.Parse(block.TransactionConfirmationData);
        QuantusHeader match = null;
        foreach(var height in SearchHeights(candidate.ObservedHeight, searchWindow, finalizedHeight))
        {
            ct.ThrowIfCancellationRequested();
            var header = await getHeader(height, ct);
            if(header == null || header.Number != height) throw new InvalidDataException("Finalized canonical header unavailable or inconsistent");
            if(header.MatchesMiningHash(candidate)) { match = header; break; }
        }
        block.ConfirmationProgress = 0;
        if(match == null) return; // A guessed height cannot prove a reward or an orphan.
        block.BlockHeight = match.Number;
        var depth = Math.Max(0, tip - match.Number + 1);
        block.ConfirmationProgress = Math.Min(1d, depth / (double)confirmations);
        if(depth < confirmations) return;
        if(!match.Matches(candidate)) { block.Status = BlockStatus.Orphaned; block.Reward = 0; return; }
        block.Reward = ParseReward(await getEvents(match.Hash, ct), wormholeAddress);
        block.Hash = match.Hash;
        block.Status = BlockStatus.Confirmed;
        block.ConfirmationProgress = 1;
        // Retain the original candidate evidence for restart and reconciliation.
    }

    internal static decimal ParseReward(string events, string wormholeAddress)
    {
        var matches = Reward.Matches(events);
        if(matches.Count != 1 || matches[0].Groups["miner"].Value != wormholeAddress)
            throw new InvalidDataException("Expected one decoded reward for the configured wormhole");
        if(!decimal.TryParse(matches[0].Groups["reward"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var raw) || raw <= 0)
            throw new InvalidDataException("Invalid Quantus reward amount");
        return raw / QuantusCli.SmallestUnit;
    }

    private static IEnumerable<long> SearchHeights(long observed, int window, uint finalized)
    {
        if(observed <= finalized) yield return observed;
        for(var d = 1; d <= window; d++)
        {
            if(observed + d <= finalized) yield return observed + d;
            if(observed - d >= 1 && observed - d <= finalized) yield return observed - d;
        }
    }
}
