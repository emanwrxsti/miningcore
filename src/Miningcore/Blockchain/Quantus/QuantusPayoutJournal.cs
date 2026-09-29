using System.Data;
using System.Text.Json;
using Dapper;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
namespace Miningcore.Blockchain.Quantus;

internal sealed class QuantusPayoutJournal
{
    private readonly IDbConnection con;
    private readonly string poolId;
    private readonly string walletKey;
    public QuantusPayoutJournal(IDbConnection con, string poolId, string walletKey)
    { this.con = con; this.poolId = poolId; this.walletKey = walletKey; }

    public static async Task CheckSchemaAsync(IDbConnection con, CancellationToken ct)
    {
        var exists = await con.ExecuteScalarAsync<bool>(new CommandDefinition(@"
            SELECT to_regclass('quantus_reward_credits') IS NOT NULL AND to_regclass('quantus_payout_batches') IS NOT NULL
            AND to_regclass('quantus_payout_one_open_wallet') IS NOT NULL AND to_regclass('quantus_payout_one_open_pool') IS NOT NULL", cancellationToken: ct));
        if(!exists) throw new InvalidOperationException("Apply src/Miningcore/Persistence/Postgres/Scripts/quantus.sql before enabling Quantus accounting");
    }
    public Task<bool> TryLockAsync(string key, CancellationToken ct) => con.ExecuteScalarAsync<bool>(new CommandDefinition(
        "SELECT pg_try_advisory_lock(hashtextextended(@key, 0))", new { key }, cancellationToken: ct));
    public Task UnlockAsync(string key) => con.ExecuteAsync("SELECT pg_advisory_unlock(hashtextextended(@key, 0))", new { key });
    public async Task<Batch> GetOpenAsync(CancellationToken ct) => await con.QuerySingleOrDefaultAsync<Batch>(new CommandDefinition(@"
        SELECT id, poolid, walletkey, state, recipients::text AS recipients, transactionhash FROM quantus_payout_batches
        WHERE (poolid = @poolId OR walletkey = @walletKey) AND state IN ('submitting', 'submitted') ORDER BY created LIMIT 1",
        new { poolId, walletKey }, cancellationToken: ct));

    public async Task<Guid> PrepareAsync(Balance[] balances, CancellationToken ct)
    {
        Validate(balances);
        var id = Guid.NewGuid();
        await con.ExecuteAsync(new CommandDefinition(@"INSERT INTO quantus_payout_batches (id, poolid, walletkey, state, recipients)
            VALUES (@id, @poolId, @walletKey, 'submitting', CAST(@recipients AS jsonb))",
            new { id, poolId, walletKey, recipients = JsonSerializer.Serialize(balances) }, cancellationToken: ct));
        return id;
    }
    public async Task StoreReceiptAsync(Guid id, string hash, CancellationToken ct)
    {
        hash = "0x" + QuantusEncoding.Hex(hash, 32);
        var changed = await con.ExecuteAsync(new CommandDefinition(@"UPDATE quantus_payout_batches SET state = 'submitted',
            transactionhash = @hash, updated = now() WHERE id = @id AND poolid = @poolId AND walletkey = @walletKey AND state = 'submitting'",
            new { id, poolId, walletKey, hash }, cancellationToken: ct));
        if(changed != 1) throw new InvalidOperationException("Payout receipt could not be journaled; manual reconciliation required");
    }
    public async Task<Balance[]> CompleteAsync(Guid id, IPaymentRepository payments, IBalanceRepository balances,
        string coin, DateTime now, CancellationToken ct)
    {
        using var tx = con.BeginTransaction();
        var batch = await con.QuerySingleAsync<Batch>(new CommandDefinition(@"SELECT id, poolid, walletkey, state,
            recipients::text AS recipients, transactionhash FROM quantus_payout_batches WHERE id = @id FOR UPDATE",
            new { id }, tx, cancellationToken: ct));
        if(batch.PoolId != poolId || batch.WalletKey != walletKey) throw new InvalidOperationException("Journal belongs to a different pool or wallet");
        if(batch.State == "credited") return [];
        if(batch.State != "submitted") throw new InvalidOperationException($"Batch {id} has an uncertain send outcome; reconcile before paying again");
        var hash = "0x" + QuantusEncoding.Hex(batch.TransactionHash, 32);
        var paid = JsonSerializer.Deserialize<Balance[]>(batch.Recipients) ?? throw new InvalidDataException("Missing recipients");
        Validate(paid);
        foreach(var balance in paid)
        {
            await payments.InsertAsync(con, tx, new Payment { PoolId = poolId, Coin = coin, Address = balance.Address,
                Amount = balance.Amount, TransactionConfirmationData = hash, Created = now });
            await balances.AddAmountAsync(con, tx, poolId, balance.Address, -balance.Amount, $"Quantus finalized payout {id}");
        }
        await con.ExecuteAsync(new CommandDefinition("UPDATE quantus_payout_batches SET state = 'credited', updated = now() WHERE id = @id", new { id }, tx, cancellationToken: ct));
        tx.Commit(); // Payment records, exact debits and completion are one atomic commit.
        return paid;
    }
    private void Validate(Balance[] balances)
    {
        if(balances.Length == 0 || balances.Any(x => x.PoolId != poolId) || balances.Select(x => x.Address).Distinct(StringComparer.Ordinal).Count() != balances.Length)
            throw new InvalidOperationException("Invalid Quantus payout batch");
        _ = QuantusCli.BatchTransfers(balances);
    }
    internal sealed class Batch
    {
        public Guid Id { get; set; }
        public string PoolId { get; set; }
        public string WalletKey { get; set; }
        public string State { get; set; }
        public string Recipients { get; set; }
        public string TransactionHash { get; set; }
    }
}
