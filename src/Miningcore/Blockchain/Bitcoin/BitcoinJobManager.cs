using System.Globalization;
using Autofac;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Org.BouncyCastle.Crypto.Parameters;

namespace Miningcore.Blockchain.Bitcoin;

public class BitcoinJobManager : BitcoinJobManagerBase<BitcoinJob>
{
    public BitcoinJobManager(
        IComponentContext ctx,
        IMasterClock clock,
        IMessageBus messageBus,
        IExtraNonceProvider extraNonceProvider) :
        base(ctx, clock, messageBus, extraNonceProvider)
    {
    }

    private const int RadiantMaxActiveJobs = 50;
    private const int RadiantFallbackJobChecks = 8;
    private BitcoinTemplate coin;

    private bool IsElektron =>
        string.Equals(coin?.Symbol, "ELEK", StringComparison.OrdinalIgnoreCase);

    private bool IsRadiant =>
        string.Equals(coin?.CanonicalName, "Radiant", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(coin?.Symbol, "RXD", StringComparison.OrdinalIgnoreCase);

    protected override object[] GetBlockTemplateParams()
    {
        if(IsElektron)
        {
            return new object[]
            {
                new
                {
                    rules = new[] { "segwit" },
                    mode = "template",
                    capabilities = new[] { "serverlist", "proposal" },
                    coinbaseaddress = poolConfig.Address
                }
            };
        }

        var result = base.GetBlockTemplateParams();
        
        if(coin.HasMWEB)
        {
            result = new object[]
            {
                new
                {
                    rules = new[] {"segwit", "mweb"},
                }
            };
        }

        if(coin.BlockTemplateRpcExtraParams != null)
        {
            if(coin.BlockTemplateRpcExtraParams.Type == JTokenType.Array)
                result = result.Concat(coin.BlockTemplateRpcExtraParams.ToObject<object[]>() ?? Array.Empty<object>()).ToArray();
            else
                result = result.Concat(new []{ coin.BlockTemplateRpcExtraParams.ToObject<object>()}).ToArray();
        }

        return result;
    }
    
    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        var syncPendingNotificationShown = false;

        do
        {
            var response = await rpc.ExecuteAsync<BlockTemplate>(logger,
                BitcoinCommands.GetBlockTemplate, ct, GetBlockTemplateParams());

            var isSynched = response.Error == null;

            if(isSynched)
            {
                logger.Info(() => "All daemons synched with blockchain");
                break;
            }
            else
            {
                logger.Debug(() => $"Daemon reports error: {response.Error?.Message}");
            }

            if(!syncPendingNotificationShown)
            {
                logger.Info(() => "Daemon is still syncing with network. Manager will be started once synced.");
                syncPendingNotificationShown = true;
            }

            await ShowDaemonSyncProgressAsync(ct);
        } while(await timer.WaitForNextTickAsync(ct));
    }

    protected async Task<RpcResponse<BlockTemplate>> GetBlockTemplateAsync(CancellationToken ct)
    {
        // Elektron's UTXO attestation is generated against the exact
        // coinbaseaddress supplied to getblocktemplate. Do not allow a
        // generic GBTArgs override to bypass it.
        var args = IsElektron
            ? (object) GetBlockTemplateParams()
            : extraPoolConfig?.GBTArgs ?? (object) GetBlockTemplateParams();

        var result = await rpc.ExecuteAsync<BlockTemplate>(logger,
            BitcoinCommands.GetBlockTemplate, ct, args);

        return result;
    }

    protected RpcResponse<BlockTemplate> GetBlockTemplateFromJson(string json)
    {
        var result = JsonConvert.DeserializeObject<JsonRpcResponse>(json);

        return new RpcResponse<BlockTemplate>(result!.ResultAs<BlockTemplate>());
    }

    private BitcoinJob CreateJob()
    {
        if(IsElektron)
            return new ElektronJob();

        return new BitcoinJob();
    }

    protected override void PostChainIdentifyConfigure()
    {
        base.PostChainIdentifyConfigure();

        if(poolConfig.EnableInternalStratum == true && coin.HeaderHasherValue is IHashAlgorithmInit hashInit)
        {
            if(!hashInit.DigestInit(poolConfig))
                logger.Error(()=> $"{hashInit.GetType().Name} initialization failed");
        }
    }

    protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct, bool forceUpdate, string via = null, string json = null)
    {
        try
        {
            if(forceUpdate)
                lastJobRebroadcast = clock.Now;

            var response = string.IsNullOrEmpty(json) ?
                await GetBlockTemplateAsync(ct) :
                GetBlockTemplateFromJson(json);

            // may happen if daemon is currently not connected to peers
            if(response.Error != null)
            {
                logger.Warn(() => $"Unable to update job. Daemon responded with: {response.Error.Message} Code {response.Error.Code}");
                return (false, forceUpdate);
            }

            var blockTemplate = response.Response;
            var job = currentJob;

            var isNew = job == null ||
                (blockTemplate != null &&
                    (job.BlockTemplate?.PreviousBlockhash != blockTemplate.PreviousBlockhash ||
                        blockTemplate.Height > job.BlockTemplate?.Height));

            if(isNew)
                messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Height, poolConfig.Template);

            if(isNew || forceUpdate)
            {
                job = CreateJob();

                job.Init(blockTemplate, NextJobId(),
                    poolConfig, extraPoolConfig, clusterConfig, clock, poolAddressDestination, network, isPoS,
                    ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue,
                    !isPoS ? coin.BlockHasherValue : coin.PoSBlockHasherValue ?? coin.BlockHasherValue);

                if(isNew)
                {
                    if(via != null)
                        logger.Info(() => $"Detected new block {blockTemplate.Height} [{via}]");
                    else
                        logger.Info(() => $"Detected new block {blockTemplate.Height}");

                    // update stats
                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight = blockTemplate.Height;
                    BlockchainStats.NetworkDifficulty = job.Difficulty;
                    BlockchainStats.NextNetworkTarget = blockTemplate.Target;
                    BlockchainStats.NextNetworkBits = blockTemplate.Bits;
                }

                else
                {
                    if(via != null)
                        logger.Debug(() => $"Template update {blockTemplate?.Height} [{via}]");
                    else
                        logger.Debug(() => $"Template update {blockTemplate?.Height}");
                }

                currentJob = job;
            }

            return (isNew, forceUpdate);
        }

        catch(OperationCanceledException)
        {
            // ignored
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
        }

        return (false, forceUpdate);
    }

    protected override object GetJobParamsForStratum(bool isNew)
    {
        var job = currentJob;
        return job?.GetJobParams(isNew);
    }

    public override BitcoinJob GetJobForStratum()
    {
        var job = currentJob;
        return job;
    }

    #region API-Surface

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<BitcoinTemplate>();
        extraPoolConfig = pc.Extra.SafeExtensionDataAs<BitcoinPoolConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing?.Extra?.SafeExtensionDataAs<BitcoinPoolPaymentProcessingConfigExtra>();

        var configuredMaxActiveJobs = extraPoolConfig?.MaxActiveJobs ?? extraPoolConfig?.MaxActiveJobsPerMiner;
        if(configuredMaxActiveJobs.HasValue && configuredMaxActiveJobs.Value > 0)
            maxActiveJobs = configuredMaxActiveJobs.Value;

        hasLegacyDaemon = extraPoolConfig?.HasLegacyDaemon == true;

        base.Configure(pc, cc);

        // Bitcoin ASICs may return buffered shares for older templates from the
        // same block. Keep more same-epoch jobs by default, while allowing an
        // explicit pool configuration to override this value.
        if(!configuredMaxActiveJobs.HasValue &&
            (string.Equals(coin?.CanonicalName, "Bitcoin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(coin?.Symbol, "BTC", StringComparison.OrdinalIgnoreCase)))
        {
            maxActiveJobs = 16;
        }

        // IceRiver RXD firmware may submit an old solution under the newest work id.
        // Keep enough historical work to recover the correct template reliably.
        if(IsRadiant)
            maxActiveJobs = RadiantMaxActiveJobs;
    }

    public virtual object[] GetSubscriberData(StratumConnection worker)
    {
        Contract.RequiresNonNull(worker);

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // Keep a unique internal connection/session id.
        context.ExtraNonce1 = extraNonceProvider.Next();

        if(IsElektron)
        {
            // Elektron coinbase must remain byte-identical to the
            // getblocktemplate coinbase commitment.
            //
            // Normal Stratum ASICs must receive:
            //   extranonce1 = ""
            //   extranonce2_size = 0
            //
            // The internal context.ExtraNonce1 above is retained only
            // for connection/share tracking and duplicate detection.
            return new object[]
            {
                string.Empty,
                0,
            };
        }

        // Standard Bitcoin-family extranonce behavior.
        var responseData = new object[]
        {
            context.ExtraNonce1,
            BitcoinConstants.ExtranoncePlaceHolderLength - ExtranonceBytes,
        };

        return responseData;
    }

    private static bool IsRecoverableRadiantWorkIdError(StratumException ex)
    {
        return ex.Code == StratumError.LowDifficultyShare ||
            ex.Code == StratumError.Other && ex.Message == "ntime out of range";
    }

    private static IEnumerable<BitcoinJob> GetRadiantJobCandidates(BitcoinJob[] activeJobs, BitcoinJob submittedJob, string nTime)
    {
        if(activeJobs == null || activeJobs.Length == 0)
            return submittedJob != null ? new[] { submittedJob } : Array.Empty<BitcoinJob>();

        var result = new List<BitcoinJob>(activeJobs.Length);
        var hasParsedNTime = uint.TryParse(nTime, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var nTimeInt);

        if(hasParsedNTime)
        {
            // Search all 50 retained jobs by timestamp. This is cheap and normally
            // identifies the exact work IceRiver was still hashing.
            foreach(var candidate in activeJobs.Reverse()
                .Where(candidate => candidate.BlockTemplate?.CurTime == nTimeInt)
                .Take(RadiantFallbackJobChecks))
            {
                result.Add(candidate);
            }
        }

        if(submittedJob != null && !result.Contains(submittedJob))
            result.Add(submittedJob);

        // A miner may roll ntime instead of returning the exact notify timestamp.
        // In that case only hash a small set of closest retained templates to avoid
        // turning malformed submissions into a 50-hash denial-of-service path.
        var fallbackJobs = activeJobs
            .Where(candidate => !result.Contains(candidate));

        fallbackJobs = hasParsedNTime ?
            fallbackJobs.OrderBy(candidate => Math.Abs((long) (candidate.BlockTemplate?.CurTime ?? 0) - nTimeInt)) :
            fallbackJobs.Reverse();

        foreach(var candidate in fallbackJobs.Take(RadiantFallbackJobChecks))
            result.Add(candidate);

        return result;
    }

    public virtual async ValueTask<Share> SubmitShareAsync(StratumConnection worker, object submission,
        CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(submission);

        if(submission is not object[] submitParams)
            throw new StratumException(StratumError.Other, "invalid params");

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // extract params
        var workerValue = (submitParams[0] as string)?.Trim();
        var jobId = submitParams[1] as string;
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;
        var versionBits = context.VersionRollingMask.HasValue ? submitParams[5] as string : null;

        if(string.IsNullOrEmpty(workerValue))
            throw new StratumException(StratumError.Other, "missing or invalid workername");

        BitcoinJob job;
        BitcoinJob[] activeJobs;
        double[] assignedDifficulties;

        lock(context)
        {
            job = context.GetJob(jobId);
            activeJobs = IsRadiant ? context.GetJobsSnapshot() : null;
            assignedDifficulties = IsRadiant ? null : context.GetJobDifficulties(jobId);
        }

        Share share = null;
        string blockHex = null;

        if(IsRadiant)
        {
            var candidates = GetRadiantJobCandidates(activeJobs, job, nTime);
            StratumException firstRecoverableError = null;
            BitcoinJob matchedJob = null;

            foreach(var candidate in candidates)
            {
                try
                {
                    (share, blockHex) = candidate.ProcessShare(worker, extraNonce2, nTime, nonce, versionBits, false);
                    matchedJob = candidate;
                    break;
                }
                catch(StratumException ex) when(IsRecoverableRadiantWorkIdError(ex))
                {
                    firstRecoverableError ??= ex;
                }
            }

            if(matchedJob == null)
            {
                if(firstRecoverableError != null)
                    throw firstRecoverableError;

                throw new StratumException(StratumError.JobNotFound, "job not found");
            }

            if(!matchedJob.RegisterRecoveredSubmit(worker, extraNonce2, nTime, nonce))
                throw new StratumException(StratumError.DuplicateShare, "duplicate share");

            if(!string.Equals(matchedJob.JobId, jobId, StringComparison.OrdinalIgnoreCase))
            {
                logger.Debug(() => $"[{worker.ConnectionId}] RXD corrected IceRiver work id {jobId} to {matchedJob.JobId} for ntime {nTime}");
            }

            job = matchedJob;
        }
        else
        {
            if(job == null)
                throw new StratumException(StratumError.JobNotFound, "job not found");

            (share, blockHex) = job.ProcessShare(worker, extraNonce2, nTime, nonce,
                versionBits, assignedDifficulties);
        }

        // enrich share with common data
        share.PoolId = poolConfig.Id;
        share.IpAddress = worker.RemoteEndpoint.Address.ToString();
        share.Miner = context.Miner;
        share.Worker = context.Worker;
        share.UserAgent = context.UserAgent;
        share.Source = clusterConfig.ClusterName;
        share.Created = clock.Now;

        // if block candidate, submit & check if accepted by network
        if(share.IsBlockCandidate)
        {
            logger.Info(() => $"Submitting block {share.BlockHeight} [{share.BlockHash}]");

            var acceptResponse = await SubmitBlockAsync(share, blockHex, ct);

            // is it still a block candidate?
            share.IsBlockCandidate = acceptResponse.Accepted;

            if(share.IsBlockCandidate)
            {
                logger.Info(() => $"Daemon accepted block {share.BlockHeight} [{share.BlockHash}] submitted by {context.Miner}");

                OnBlockFound();

                // persist the coinbase transaction-hash to allow the payment processor
                // to verify later on that the pool has received the reward for the block
                share.TransactionConfirmationData = acceptResponse.CoinbaseTx;
            }

            else
            {
                // clear fields that no longer apply
                share.TransactionConfirmationData = null;
            }
        }

        return share;
    }

    public double ShareMultiplier => coin.ShareMultiplier;

    #endregion // API-Surface
}
