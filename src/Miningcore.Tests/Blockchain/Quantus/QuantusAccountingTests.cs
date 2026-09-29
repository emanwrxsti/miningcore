using System;
using System.Data;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AutoMapper;
using Dapper;
using Miningcore.Blockchain.Quantus;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using NLog;
using Npgsql;
using NSubstitute;
using Xunit;
namespace Miningcore.Tests.Blockchain.Quantus;

public class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUANTUS_TEST_POSTGRES")))
        {
            if(Environment.GetEnvironmentVariable("QUANTUS_REQUIRE_INTEGRATION")=="1")
                throw new InvalidOperationException("QUANTUS_TEST_POSTGRES is required for the integration gate");
            Skip="Set QUANTUS_TEST_POSTGRES to an isolated test database; tests create and drop their own schemas";
        }
    }
}
public sealed class PostgresConcurrencyFactAttribute : PostgresFactAttribute
{
    public PostgresConcurrencyFactAttribute()
    {
        if(Environment.GetEnvironmentVariable("QUANTUS_TEST_SINGLE_SESSION")=="1")
            Skip="Requires independent PostgreSQL sessions; the WASM backend cannot test locking";
    }
}
public class QuantusAccountingTests
{
    [PostgresFact] public async Task CandidatePersistsWithRequiredConfirmationEvidence()
    {
        await using var db=await Database.CreateAsync();var block=Candidate();
        await db.Blocks.InsertAsync(db.Con,null,block);
        var stored=await db.Con.QuerySingleAsync<string>("SELECT transactionconfirmationdata FROM blocks");
        Assert.Equal(block.TransactionConfirmationData,stored);
        Assert.Equal(QuantusCandidate.Parse(stored).Id,await db.Con.QuerySingleAsync<string>("SELECT hash FROM blocks"));
    }
    [PostgresFact] public async Task CanonicalRewardCreditsOnlyOneCandidateRow()
    {
        await using var db=await Database.CreateAsync();var first=await db.InsertCandidateAsync();var duplicate=await db.InsertCandidateAsync();
        var handler=new TestHandler(db);
        using(var tx=db.Con.BeginTransaction())
        {
            Assert.True(await handler.TryBeginBlockPaymentAsync(db.Con,tx,first,default));
            await db.Balances.AddAmountAsync(db.Con,tx,"quantus-tests",QuantusConsensusTests.Address,5,"Test scheme credit");
            await db.Blocks.UpdateBlockAsync(db.Con,tx,first);tx.Commit();
        }
        using(var tx=db.Con.BeginTransaction()) { Assert.False(await handler.TryBeginBlockPaymentAsync(db.Con,tx,duplicate,default));tx.Commit(); }
        Assert.Equal(5,await db.Balances.GetBalanceAsync(db.Con,"quantus-tests",QuantusConsensusTests.Address));
        Assert.Equal(1,await db.Con.ExecuteScalarAsync<int>("SELECT count(*) FROM quantus_reward_credits"));
        Assert.Equal("orphaned",await db.Con.QuerySingleAsync<string>("SELECT status FROM blocks WHERE id=@id",new{id=duplicate.Id}));
        first.Status=BlockStatus.Pending;
        using(var tx=db.Con.BeginTransaction()) Assert.False(await handler.TryBeginBlockPaymentAsync(db.Con,tx,first,default));
    }
    [PostgresFact] public async Task RewardFailureRollsBackClaimAndBalancesTogether()
    {
        await using var db=await Database.CreateAsync();var block=await db.InsertCandidateAsync();var handler=new TestHandler(db);
        using(var tx=db.Con.BeginTransaction())
        {
            Assert.True(await handler.TryBeginBlockPaymentAsync(db.Con,tx,block,default));
            await db.Balances.AddAmountAsync(db.Con,tx,"quantus-tests",QuantusConsensusTests.Address,5,"Interrupted credit");tx.Rollback();
        }
        Assert.Equal(0,await db.Con.ExecuteScalarAsync<int>("SELECT count(*) FROM quantus_reward_credits"));
        Assert.Equal(0,await db.Con.ExecuteScalarAsync<int>("SELECT count(*) FROM balances"));
        using(var tx=db.Con.BeginTransaction()) { Assert.True(await handler.TryBeginBlockPaymentAsync(db.Con,tx,block,default));tx.Rollback(); }
    }
    [PostgresFact] public async Task UncertainPayoutPreventsNewBatchAndBalanceDebit()
    {
        await using var db=await Database.CreateAsync();var journal=db.Journal();var id=await journal.PrepareAsync(Paid(),default);
        Assert.Equal("submitting",(await db.Journal().GetOpenAsync(default)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>journal.CompleteAsync(id,db.Payments,db.Balances,"QTC",DateTime.UtcNow,default));
        var collision=await Assert.ThrowsAsync<PostgresException>(()=>journal.PrepareAsync(Paid(),default));Assert.Equal("23505",collision.SqlState);
        var changedWallet=new QuantusPayoutJournal(db.Con,"quantus-tests","other-wallet");
        Assert.Equal(id,(await changedWallet.GetOpenAsync(default)).Id);
        await Assert.ThrowsAsync<PostgresException>(()=>changedWallet.PrepareAsync(Paid(),default));
        Assert.Equal(0,await db.Con.ExecuteScalarAsync<int>("SELECT count(*) FROM payments"));
    }
    [PostgresFact] public async Task FinalizedRecoveryDebitsOnceAndPreservesNewAccrual()
    {
        await using var db=await Database.CreateAsync();
        await db.Balances.AddAmountAsync(db.Con,null,"quantus-tests",QuantusConsensusTests.Address,12,"Balance plus new accrual");
        var journal=db.Journal();var id=await journal.PrepareAsync(Paid(),default);
        await journal.StoreReceiptAsync(id,"0x"+new string('a',64),default);
        Assert.Single(await db.Journal().CompleteAsync(id,db.Payments,db.Balances,"QTC",DateTime.UtcNow,default));
        Assert.Equal(2,await db.Balances.GetBalanceAsync(db.Con,"quantus-tests",QuantusConsensusTests.Address));
        Assert.Empty(await db.Journal().CompleteAsync(id,db.Payments,db.Balances,"QTC",DateTime.UtcNow,default));
        Assert.Equal(1,await db.Con.ExecuteScalarAsync<int>("SELECT count(*) FROM payments"));
        Assert.Equal(10,await db.Con.ExecuteScalarAsync<decimal>("SELECT amount FROM payments"));Assert.Null(await journal.GetOpenAsync(default));
    }
    [PostgresFact] public async Task FailedPaymentPersistenceRollsBackBeforeRecovery()
    {
        await using var db=await Database.CreateAsync();
        await db.Balances.AddAmountAsync(db.Con,null,"quantus-tests",QuantusConsensusTests.Address,12,"Balance");
        var journal=db.Journal();var id=await journal.PrepareAsync(Paid(),default);await journal.StoreReceiptAsync(id,"0x"+new string('a',64),default);
        var failing=Substitute.For<IBalanceRepository>();
        failing.AddAmountAsync(Arg.Any<IDbConnection>(),Arg.Any<IDbTransaction>(),Arg.Any<string>(),Arg.Any<string>(),Arg.Any<decimal>(),Arg.Any<string>(),Arg.Any<string[]>())
            .Returns(Task.FromException<int>(new IOException("Simulated DB failure")));
        await Assert.ThrowsAsync<IOException>(()=>journal.CompleteAsync(id,db.Payments,failing,"QTC",DateTime.UtcNow,default));
        Assert.Equal(0,await db.Con.ExecuteScalarAsync<int>("SELECT count(*) FROM payments"));
        Assert.Equal("submitted",(await journal.GetOpenAsync(default)).State);
        Assert.Equal(12,await db.Balances.GetBalanceAsync(db.Con,"quantus-tests",QuantusConsensusTests.Address));
        await journal.CompleteAsync(id,db.Payments,db.Balances,"QTC",DateTime.UtcNow,default);
        Assert.Equal(2,await db.Balances.GetBalanceAsync(db.Con,"quantus-tests",QuantusConsensusTests.Address));
        Assert.Equal(1,await db.Con.ExecuteScalarAsync<int>("SELECT count(*) FROM payments"));
    }
    [PostgresConcurrencyFact] public async Task WalletLockExcludesOtherProcessesUntilRelease()
    {
        await using var db=await Database.CreateAsync();await using var other=new NpgsqlConnection(db.Con.ConnectionString);await other.OpenAsync();
        var first=db.Journal();var second=new QuantusPayoutJournal(other,"quantus-tests","wallet");var key="quantus-test-"+Guid.NewGuid();
        Assert.True(await first.TryLockAsync(key,default));Assert.False(await second.TryLockAsync(key,default));
        await first.UnlockAsync(key);Assert.True(await second.TryLockAsync(key,default));await second.UnlockAsync(key);
    }
    private static Balance[] Paid()=>new[]{new Balance{PoolId="quantus-tests",Address=QuantusConsensusTests.Address,Amount=10}};
    private static Block Candidate()=>QuantusConsensusTests.BlockFor(QuantusConsensusTests.Candidate(QuantusConsensusTests.Header(100),100));
    private sealed class TestHandler : QuantusPayoutHandler
    {
        public TestHandler(Database db):base(Substitute.For<IConnectionFactory>(),db.Mapper,Substitute.For<IShareRepository>(),db.Blocks,db.Balances,db.Payments,Substitute.For<IMasterClock>(),Substitute.For<IMessageBus>())
        {
            poolConfig=new PoolConfig{Id="quantus-tests"};logger=LogManager.GetLogger("Quantus.Tests");
            typeof(QuantusPayoutHandler).GetField("genesisHash",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(this,"0x"+new string('0',64));
        }
    }
    private sealed class Database : IAsyncDisposable
    {
        private readonly string schema="quantus_test_"+Guid.NewGuid().ToString("N");
        public NpgsqlConnection Con {get;private set;}
        public IMapper Mapper {get;}=new MapperConfiguration(x=>x.AddProfile<AutoMapperProfile>()).CreateMapper();
        public BlockRepository Blocks=>new(Mapper);
        public BalanceRepository Balances=>new(Mapper);
        public PaymentRepository Payments=>new(Mapper);
        public QuantusPayoutJournal Journal()=>new(Con,"quantus-tests","wallet");
        public static async Task<Database> CreateAsync()
        {
            var db=new Database();var builder=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("QUANTUS_TEST_POSTGRES")){Pooling=false,SearchPath=db.schema,IncludeErrorDetail=true};
            db.Con=new NpgsqlConnection(builder.ConnectionString);
            try
            {
                await db.Con.OpenAsync();await db.Con.ExecuteAsync($"CREATE SCHEMA {db.schema}");
                await db.Con.ExecuteAsync($"SET search_path TO {db.schema}");
                var path=Path.Combine(AppContext.BaseDirectory,"QuantusSql");
                await db.Con.ExecuteAsync((await File.ReadAllTextAsync(Path.Combine(path,"createdb.sql"))).Replace("SET ROLE miningcore;",""));
                await db.Con.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(path,"quantus.sql")));
                await QuantusPayoutJournal.CheckSchemaAsync(db.Con,default);return db;
            }
            catch { await db.Con.DisposeAsync();throw; }
        }
        public async Task<Block> InsertCandidateAsync()
        {
            var b=Candidate();await Blocks.InsertAsync(Con,null,b);b.Id=await Con.ExecuteScalarAsync<long>("SELECT max(id) FROM blocks");
            b.Hash=QuantusConsensusTests.Header(100).Hash;b.Status=BlockStatus.Confirmed;b.Reward=5;return b;
        }
        public async ValueTask DisposeAsync()
        {
            if(Con==null) return;
            try { await Con.ExecuteAsync($"DROP SCHEMA {schema} CASCADE"); }
            finally { await Con.DisposeAsync(); }
        }
    }
}
