using Autofac;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Rpc;
using Newtonsoft.Json.Linq;
using NLog;

namespace Miningcore.Blockchain.BedrockCoin
{
    /// <summary>
    /// Custom Job Manager for BedrockCoin (hybrid PoW/PoS - difficulty fix)
    /// </summary>
    public class BedrockJobManager : BitcoinJobManager
    {
        private readonly RpcClient rpc;

        public BedrockJobManager(
            IComponentContext ctx,
            IMasterClock clock,
            IMessageBus messageBus,
            IExtraNonceProvider extraNonceProvider)
            : base(ctx, clock, messageBus, extraNonceProvider)
        {
            rpc = ctx.Resolve<RpcClient>();
        }

        /// <summary>
        /// Override UpdateJob to normalize BedrockCoin’s hybrid difficulty format.
        /// </summary>
        protected override async Task<(bool IsNew, bool Force)> UpdateJob(
            CancellationToken ct, bool forceUpdate = false, string via = null, string json = null)
        {
            // Fetch mining info
            var resp = await rpc.ExecuteAsync<JObject>(
                logger,
                "getmininginfo",
                ct);

            var result = resp?.Response;
            if (result != null && result["difficulty"] != null)
            {
                var diffToken = result["difficulty"];
                double difficulty = 0d;

                if (diffToken.Type == JTokenType.Object)
                    difficulty = diffToken["proof-of-work"]?.Value<double>() ?? 0d;
                else
                    difficulty = diffToken.Value<double>();

                // Replace hybrid difficulty object with a single numeric value
                result["difficulty"] = difficulty;
            }

            // Continue normal Bitcoin job update
            return await base.UpdateJob(ct, forceUpdate, via, json);
        }
    }
}
