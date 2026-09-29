using System.Data;
using AutoMapper;
using Dapper;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using Block = Miningcore.Persistence.Model.Block;
namespace Miningcore.Blockchain.Quantus;

[CoinFamily(CoinFamily.Quantus)]
public class QuantusPayoutHandler : PayoutHandlerBase, IPayoutHandler, IBlockPayoutGuard
{
    public QuantusPayoutHandler(IConnectionFactory cf, IMapper mapper, IShareRepository shareRepo,
        IBlockRepository blockRepo, IBalanceRepository balanceRepo, IPaymentRepository paymentRepo,
        IMasterClock clock, IMessageBus messageBus) : base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus) { }
    private QuantusPaymentProcessingConfigExtra extra;
    private QuantusRpcClient rpcClient;
    private QuantusCli cli;
    private string genesisHash;
    private int minimumConfirmations;
    protected override string LogCategory => "Quantus Payout Handler";

    public async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        poolConfig = pc ?? throw new ArgumentNullException(nameof(pc)); clusterConfig = cc;
        logger = LogUtil.GetPoolScopedLogger(typeof(QuantusPayoutHandler), pc);
        extra = pc.PaymentProcessing?.Extra.SafeExtensionDataAs<QuantusPaymentProcessingConfigExtra>() ?? new();
        QuantusPow.EnsureAvailable();
        if(!QuantusEncoding.IsAddress(extra.WormholeAddress)) throw new InvalidOperationException("wormholeAddress must be a valid network-189 SS58 address");
        if(!Path.IsPathFullyQualified(extra.CliPath) || !File.Exists(extra.CliPath)) throw new InvalidOperationException("cliPath must be an absolute installed executable path");
        genesisHash = "0x" + QuantusEncoding.Hex(extra.GenesisHash, 32);
        minimumConfirmations = extra.MinimumConfirmations ?? 20;
        if(minimumConfirmations < 1 || minimumConfirmations > 10000 || extra.CandidateSearchWindow < 1 || extra.CandidateSearchWindow > 128 ||
            extra.BatchSize < 1 || extra.BatchSize > 100 || extra.CliTimeoutSeconds < 30 || extra.CliTimeoutSeconds > 7200 || extra.WormholeIndex < 0)
            throw new InvalidOperationException("Invalid Quantus confirmation/search/batch/timeout/index setting");
        if(pc.RewardRecipients?.Any(x => x.Percentage < 0 || x.Percentage > 100 || !QuantusEncoding.IsAddress(x.Address)) == true || pc.RewardRecipients?.Sum(x => x.Percentage) > 100)
            throw new InvalidOperationException("Reward recipients need valid addresses and a total percentage at most 100");
        var daemon = pc.Daemons?.FirstOrDefault() ?? throw new InvalidOperationException("Quantus daemon is missing");
        var rpcUri = new UriBuilder(daemon.Ssl ? "https" : "http", daemon.Host, daemon.Port, daemon.HttpPath ?? "").Uri;
        var wsUri = new UriBuilder(rpcUri) { Scheme = daemon.Ssl ? "wss" : "ws" }.Uri;
        var configured = string.IsNullOrWhiteSpace(extra.NodeUrl) ? wsUri : new Uri(extra.NodeUrl);
        if(configured.Scheme != wsUri.Scheme || configured.Host != wsUri.Host || configured.Port != wsUri.Port ||
            configured.AbsolutePath != wsUri.AbsolutePath || configured.Query != wsUri.Query || configured.UserInfo.Length != 0)
            throw new InvalidOperationException("nodeUrl must use the same host, port and path as the configured RPC daemon");
        rpcClient?.Dispose(); rpcClient = new QuantusRpcClient(rpcUri.AbsoluteUri);
        if(await rpcClient.GetBlockHashAsync(0, ct) != genesisHash) throw new InvalidOperationException("RPC genesis hash differs from configured network");
        cli = new QuantusCli(extra, configured.AbsoluteUri);
        await cli.VerifyBinaryAsync(ct);
        using(var con = await cf.OpenConnectionAsync()) await QuantusPayoutJournal.CheckSchemaAsync(con, ct);
        if(extra.EnablePayouts)
        {
            if(!QuantusEncoding.IsAddress(pc.Address) || string.IsNullOrWhiteSpace(extra.WalletName)) throw new InvalidOperationException("Payouts require valid pool address and walletName");
            ValidateSecretFile(extra.PasswordFile);
            if(extra.CollectRewards && !string.IsNullOrWhiteSpace(extra.WormholeSecretFile)) ValidateSecretFile(extra.WormholeSecretFile);
        }
    }
    private static void ValidateSecretFile(string path)
    {
        if(string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new InvalidOperationException("Credential files need existing absolute paths");
        if(!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) throw new InvalidOperationException("Credential files require owner-only permissions (chmod 600)");
    }
    public async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        if(blocks.Length == 0) return blocks;
        var tip = await rpcClient.GetTipHeightAsync(ct);
        var finalized = await rpcClient.GetFinalizedHeaderAsync(ct);
        var cache = new Dictionary<long, QuantusHeader>();
        async Task<QuantusHeader> GetHeader(long height, CancellationToken token)
        {
            if(cache.TryGetValue(height, out var cached)) return cached;
            var hash = await rpcClient.GetBlockHashAsync(height, token) ?? throw new InvalidDataException("Finalized block hash unavailable");
            return cache[height] = await rpcClient.GetHeaderAsync(hash, token);
        }
        foreach(var block in blocks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await QuantusBlockClassifier.ClassifyAsync(block, tip, finalized.Number, minimumConfirmations, extra.CandidateSearchWindow,
                    extra.WormholeAddress, GetHeader, cli.QueryEventsAsync, ct);
                if(block.Status == BlockStatus.Pending)
                {
                    logger.Info($"Quantus candidate {block.Id} remains pending: canonical evidence or confirmation depth is incomplete");
                    messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, poolConfig.Template);
                }
                else messageBus.NotifyBlockUnlocked(poolConfig.Id, block, poolConfig.Template);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception ex)
            {
                block.Status = BlockStatus.Pending; block.Reward = 0;
                logger.Warn(ex, $"Quantus candidate {block.Id} needs retry or reconciliation; keeping Pending");
            }
        }
        return blocks;
    }
    public async Task<bool> TryBeginBlockPaymentAsync(IDbConnection con, IDbTransaction tx, Block block, CancellationToken ct)
    {
        // Runs in the same transaction as the payment scheme, credits and block update.
        await con.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
            new { key = "quantus:rewards:" + poolConfig.Id }, tx, cancellationToken: ct));
        var current = await con.QuerySingleAsync<string>(new CommandDefinition("SELECT status FROM blocks WHERE id = @id AND poolid = @poolId FOR UPDATE",
            new { id = block.Id, poolId = poolConfig.Id }, tx, cancellationToken: ct));
        if(current != "pending") return false;
        if(block.Status != BlockStatus.Confirmed) return true;
        _ = QuantusCandidate.Parse(block.TransactionConfirmationData);
        var hash = "0x" + QuantusEncoding.Hex(block.Hash, 32);
        var claimed = await con.ExecuteAsync(new CommandDefinition(@"INSERT INTO quantus_reward_credits (genesishash, blockhash, poolid, blockid)
            VALUES (@genesisHash, @hash, @poolId, @blockId) ON CONFLICT DO NOTHING", new { genesisHash, hash, poolId = poolConfig.Id, blockId = block.Id }, tx, cancellationToken: ct));
        if(claimed == 1) return true;
        block.Status = BlockStatus.Orphaned; block.Reward = 0;
        await blockRepo.UpdateBlockAsync(con, tx, block);
        logger.Warn($"Candidate {block.Id} duplicates an already credited Quantus block {hash}; skipped");
        return false;
    }
    public async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        if(!extra.EnablePayouts) { logger.Info("Quantus sending disabled; verified rewards remain in miner balances"); return; }
        using var con = await cf.OpenConnectionAsync();
        var walletKey = genesisHash + ":" + poolConfig.Address;
        var journal = new QuantusPayoutJournal(con, poolConfig.Id, walletKey);
        var acquired = new List<string>();
        try
        {
            foreach(var key in new[] { "quantus:pool:" + poolConfig.Id, "quantus:wallet:" + walletKey })
            { if(!await journal.TryLockAsync(key, ct)) return; acquired.Add(key); }
            var open = await journal.GetOpenAsync(ct);
            if(open != null)
            {
                if(open.State != "submitted" || open.PoolId != poolConfig.Id || open.WalletKey != walletKey)
                    throw new InvalidOperationException($"Payout batch {open.Id} is unresolved; reconcile its journal before resuming sends");
                var recovered = await journal.CompleteAsync(open.Id, paymentRepo, balanceRepo, poolConfig.Template.Symbol, clock.Now, ct);
                if(recovered.Length > 0) NotifyPayoutSuccess(poolConfig.Id, recovered, [open.TransactionHash], null);
            }
            await cli.VerifyWalletAsync(poolConfig.Address, ct);
            if(extra.CollectRewards) await cli.CollectRewardsAsync(poolConfig.Address, (await rpcClient.GetFinalizedHeaderAsync(ct)).Number, ct);
            // Caller snapshots can predate a recovery or another process. Read under the locks.
            var fresh = await balanceRepo.GetPoolBalancesOverThresholdAsync(con, poolConfig.Id, poolConfig.PaymentProcessing.MinimumPayment);
            foreach(var batch in fresh.OrderBy(x => x.Address, StringComparer.Ordinal).Chunk(extra.BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var id = await journal.PrepareAsync(batch, ct);
                logger.Info($"Quantus payout intent {id} recorded for {batch.Length} recipients");
                var hash = await cli.SendBatchAsync(batch, ct);
                logger.Info($"Quantus CLI reported finalized payout {id}: {hash}");
                using(var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15))) await journal.StoreReceiptAsync(id, hash, deadline.Token);
                var paid = await journal.CompleteAsync(id, paymentRepo, balanceRepo, poolConfig.Template.Symbol, clock.Now, ct);
                NotifyPayoutSuccess(poolConfig.Id, paid, [hash], null);
            }
        }
        finally
        {
            foreach(var key in acquired.AsEnumerable().Reverse())
            { try { await journal.UnlockAsync(key); } catch(Exception ex) { logger.Warn(ex, "Payout connection closed before lock release"); } }
        }
    }
    public double AdjustBlockEffort(double effort) => effort;
}
