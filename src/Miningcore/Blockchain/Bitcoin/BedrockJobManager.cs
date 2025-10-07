using System;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Stratum;
using Newtonsoft.Json.Linq;
using NLog;

namespace Miningcore.Blockchain.BedrockCoin
{
    /// <summary>
    /// Custom Job Manager for BedrockCoin (PoW + PoS hybrid difficulty fix)
    /// </summary>
    public class BedrockJobManager : BitcoinJobManager
    {
        public BedrockJobManager(
            IComponentContext ctx,
            ILogger logger) : base(ctx, logger)
        {
        }

        protected override async Task UpdateJob(CancellationToken ct)
        {
            var result = await daemon.ExecuteCmdAnyAsync<JObject>(logger, BitcoinCommands.GetMiningInfo, ct);

            if (result != null)
            {
                // BedrockCoin returns difficulty as object: { "proof-of-work": ..., "proof-of-stake": ... }
                var diffToken = result["difficulty"];
                double difficulty;

                if (diffToken?.Type == JTokenType.Object)
                    difficulty = diffToken["proof-of-work"]?.Value<double>() ?? 0d;
                else
                    difficulty = diffToken?.Value<double>() ?? 0d;

                result["difficulty"] = difficulty;
            }

            await base.UpdateJob(ct);
        }
    }
}
