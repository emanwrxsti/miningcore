using System.Globalization;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Messaging;
using NLog;
using StackExchange.Redis;

namespace Miningcore.Mining;

public sealed class MinerWorkerShareStats
{
    public string MinerWorker { get; set; }
    public string Worker { get; set; }
    public long Accepted { get; set; }
    public long Rejected { get; set; }
    public long Stale { get; set; }
    public long Retargets { get; set; }
    public double? CurrentDifficulty { get; set; }
    public DateTime? LastShare { get; set; }
    public DateTime? LastReject { get; set; }
    public DateTime? LastStale { get; set; }
    public DateTime? LastRetarget { get; set; }
}

public sealed class MinerShareStatsSnapshot
{
    public string Address { get; set; }
    public long Accepted { get; set; }
    public long Rejected { get; set; }
    public long Stale { get; set; }
    public long Retargets { get; set; }
    public DateTime? LastShare { get; set; }
    public DateTime? LastReject { get; set; }
    public DateTime? LastStale { get; set; }
    public DateTime? LastRetarget { get; set; }
    public MinerWorkerShareStats[] Workers { get; set; } = Array.Empty<MinerWorkerShareStats>();
}

internal enum MinerStatEventType
{
    Connected,
    Heartbeat,
    Disconnected,
    Accepted,
    Rejected,
    Stale,
    Retarget
}

internal readonly record struct MinerStatEvent(
    MinerStatEventType Type,
    string PoolId,
    string Miner,
    string Worker,
    string SessionId,
    double? Difficulty,
    DateTime Created);

internal sealed class MinerStatAggregate
{
    public long Accepted;
    public long Rejected;
    public long Stale;
    public long Retargets;
    public double? CurrentDifficulty;
    public long? LastShareUnixMs;
    public long? LastRejectUnixMs;
    public long? LastStaleUnixMs;
    public long? LastRetargetUnixMs;
    public long? FirstActivityUnixMs;
    public long? LastActivityUnixMs;
}

/// <summary>
/// Multi-instance live miner telemetry backed by Redis.
///
/// Every authorized Stratum socket gets a globally unique session id. Active
/// sessions are stored in a Redis sorted-set with their heartbeat expiry as the
/// score. This lets several Miningcore instances/regions share the same Redis
/// safely. A worker's live counters are reset only when the first session after
/// a globally-offline period connects. The last global disconnect removes the
/// worker from the live worker set and leaves only a short-lived closed hash.
/// </summary>
public sealed class MinerStatsRedisService : BackgroundService
{
    public MinerStatsRedisService(ClusterConfig clusterConfig, IMessageBus messageBus)
    {
        this.messageBus = messageBus;
        config = clusterConfig.Persistence?.Redis;

        var configuredNodeName = config?.NodeName?.Trim();
        var fallbackNodeName = $"{clusterConfig.ClusterName ?? "miningcore"}-{clusterConfig.InstanceId?.ToString(CultureInfo.InvariantCulture) ?? "0"}";
        nodeName = string.IsNullOrWhiteSpace(configuredNodeName) ? fallbackNodeName : configuredNodeName;
        bootId = Guid.NewGuid().ToString("N")[..12];

        queue = Channel.CreateBounded<MinerStatEvent>(new BoundedChannelOptions(100_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IMessageBus messageBus;
    private readonly RedisConfig config;
    private readonly Channel<MinerStatEvent> queue;
    private readonly string nodeName;
    private readonly string bootId;
    private ConnectionMultiplexer redis;
    private IDatabase db;
    private long droppedEvents;

    private const string TouchSessionScript = @"
local now = tonumber(ARGV[1])
local expiresAt = tonumber(ARGV[2])
local sessionId = ARGV[3]
local workerName = ARGV[4]
local workerHash = ARGV[5]
local liveTtl = tonumber(ARGV[6])
local sessionKeyTtl = tonumber(ARGV[7])

redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
local count = redis.call('ZCARD', KEYS[1])

if count == 0 then
  redis.call('HDEL', KEYS[2],
    'accepted', 'rejected', 'stale', 'retargets',
    'currentDifficulty', 'lastShare', 'lastReject', 'lastStale',
    'lastRetarget', 'lastActivity', 'closedAt')
  redis.call('HSET', KEYS[2], 'sessionStarted', now)
end

redis.call('ZADD', KEYS[1], expiresAt, sessionId)
redis.call('SADD', KEYS[3], workerHash)
redis.call('HSET', KEYS[2], 'worker', workerName, 'lastActivity', now)
redis.call('HSETNX', KEYS[2], 'firstSeen', now)
redis.call('PEXPIRE', KEYS[1], sessionKeyTtl)
redis.call('PEXPIRE', KEYS[2], liveTtl)
redis.call('PEXPIRE', KEYS[3], liveTtl)
return count
";

    private const string DisconnectSessionScript = @"
local now = tonumber(ARGV[1])
local sessionId = ARGV[2]
local workerName = ARGV[3]
local workerHash = ARGV[4]
local closedTtl = tonumber(ARGV[5])
local liveTtl = tonumber(ARGV[6])
local sessionKeyTtl = tonumber(ARGV[7])

redis.call('ZREM', KEYS[1], sessionId)
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
local count = redis.call('ZCARD', KEYS[1])

if count == 0 then
  redis.call('SREM', KEYS[3], workerHash)
  redis.call('DEL', KEYS[1])
  redis.call('HSET', KEYS[2], 'worker', workerName, 'closedAt', now)
  redis.call('PEXPIRE', KEYS[2], closedTtl)
  if redis.call('SCARD', KEYS[3]) == 0 then
    redis.call('DEL', KEYS[3])
  end
else
  redis.call('PEXPIRE', KEYS[1], sessionKeyTtl)
  redis.call('PEXPIRE', KEYS[2], liveTtl)
  redis.call('PEXPIRE', KEYS[3], liveTtl)
end

return count
";

    private const string UpdateWorkerScript = @"
local now = tonumber(ARGV[1])
local workerHash = ARGV[2]
local workerName = ARGV[3]
local accepted = tonumber(ARGV[4])
local rejected = tonumber(ARGV[5])
local stale = tonumber(ARGV[6])
local retargets = tonumber(ARGV[7])
local lastActivity = tonumber(ARGV[8])
local lastShare = tonumber(ARGV[9])
local lastReject = tonumber(ARGV[10])
local lastStale = tonumber(ARGV[11])
local lastRetarget = tonumber(ARGV[12])
local difficulty = ARGV[13]
local liveTtl = tonumber(ARGV[14])
local closedTtl = tonumber(ARGV[15])

redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
if redis.call('ZCARD', KEYS[1]) == 0 then
  redis.call('SREM', KEYS[3], workerHash)
  redis.call('DEL', KEYS[1])
  redis.call('HSET', KEYS[2], 'worker', workerName, 'closedAt', now)
  redis.call('PEXPIRE', KEYS[2], closedTtl)
  if redis.call('SCARD', KEYS[3]) == 0 then
    redis.call('DEL', KEYS[3])
  end
  return 0
end

local sessionStarted = tonumber(redis.call('HGET', KEYS[2], 'sessionStarted') or '0')
if lastActivity > 0 and sessionStarted > 0 and lastActivity < sessionStarted then
  return 0
end

if accepted > 0 then redis.call('HINCRBY', KEYS[2], 'accepted', accepted) end
if rejected > 0 then redis.call('HINCRBY', KEYS[2], 'rejected', rejected) end
if stale > 0 then redis.call('HINCRBY', KEYS[2], 'stale', stale) end
if retargets > 0 then redis.call('HINCRBY', KEYS[2], 'retargets', retargets) end

local function setmax(field, value)
  if value <= 0 then return end
  local old = tonumber(redis.call('HGET', KEYS[2], field) or '0')
  if value > old then redis.call('HSET', KEYS[2], field, value) end
end

setmax('lastActivity', lastActivity)
setmax('lastShare', lastShare)
setmax('lastReject', lastReject)
setmax('lastStale', lastStale)
setmax('lastRetarget', lastRetarget)

if difficulty ~= '' then redis.call('HSET', KEYS[2], 'currentDifficulty', difficulty) end
redis.call('HSET', KEYS[2], 'worker', workerName)
redis.call('SADD', KEYS[3], workerHash)
redis.call('PEXPIRE', KEYS[2], liveTtl)
redis.call('PEXPIRE', KEYS[3], liveTtl)
return 1
";

    public bool Enabled => config?.Enabled == true;

    public TimeSpan SessionHeartbeatInterval =>
        TimeSpan.FromSeconds(config?.SessionHeartbeatSeconds is > 0 ? config.SessionHeartbeatSeconds.Value : 30);

    public string CreateSessionId(string connectionId) => $"{nodeName}:{bootId}:{connectionId}";

    public void RecordConnected(string poolId, WorkerContextBase context)
    {
        Queue(MinerStatEventType.Connected, poolId, context, null);
    }

    public void RecordHeartbeat(string poolId, WorkerContextBase context)
    {
        Queue(MinerStatEventType.Heartbeat, poolId, context, context?.Difficulty);
    }

    public void RecordDisconnected(string poolId, WorkerContextBase context)
    {
        Queue(MinerStatEventType.Disconnected, poolId, context, null);
    }

    public void RecordRejected(string poolId, WorkerContextBase context, double? difficulty = null)
    {
        Queue(MinerStatEventType.Rejected, poolId, context, difficulty ?? context?.Difficulty);
    }

    public void RecordStale(string poolId, WorkerContextBase context, double? difficulty = null)
    {
        Queue(MinerStatEventType.Stale, poolId, context, difficulty ?? context?.Difficulty);
    }

    public void RecordRetarget(string poolId, WorkerContextBase context, double newDifficulty)
    {
        Queue(MinerStatEventType.Retarget, poolId, context, newDifficulty);
    }

    private void RecordAccepted(Share share)
    {
        if(share == null || string.IsNullOrWhiteSpace(share.PoolId) || string.IsNullOrWhiteSpace(share.Miner))
            return;

        Queue(new MinerStatEvent(
            MinerStatEventType.Accepted,
            share.PoolId,
            share.Miner,
            NormalizeWorker(share.Worker),
            null,
            share.Difficulty,
            share.Created == default ? DateTime.UtcNow : share.Created));
    }

    private void Queue(MinerStatEventType type, string poolId, WorkerContextBase context, double? difficulty)
    {
        if(!Enabled || context == null || string.IsNullOrWhiteSpace(poolId) || string.IsNullOrWhiteSpace(context.Miner))
            return;

        Queue(new MinerStatEvent(
            type,
            poolId,
            context.Miner,
            NormalizeWorker(context.Worker),
            context.RedisSessionId,
            difficulty,
            DateTime.UtcNow));
    }

    private void Queue(MinerStatEvent evt)
    {
        if(!Enabled)
            return;

        if(!queue.Writer.TryWrite(evt))
        {
            var dropped = Interlocked.Increment(ref droppedEvents);
            if(dropped == 1 || dropped % 1000 == 0)
                logger.Warn(() => $"Redis miner-stats queue full. Dropped {dropped} event(s)");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!Enabled)
        {
            logger.Info("Redis miner stats disabled");
            return;
        }

        if(string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            logger.Error("Redis miner stats enabled but persistence.redis.connectionString is empty");
            return;
        }

        IDisposable acceptedSubscription = null;

        try
        {
            var options = ConfigurationOptions.Parse(config.ConnectionString);
            options.AbortOnConnectFail = false;

            redis = await ConnectionMultiplexer.ConnectAsync(options);
            db = redis.GetDatabase(config.Database);

            acceptedSubscription = messageBus.Listen<Share>()
                .Where(x => x != null)
                .Subscribe(RecordAccepted,
                    ex => logger.Error(ex, "Redis miner-stats accepted-share subscription failed"));

            logger.Info(() => $"Redis miner stats online (db={config.Database}, prefix={Prefix}, node={nodeName})");

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

            while(await timer.WaitForNextTickAsync(ct))
                await FlushAsync(ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch(Exception ex)
        {
            logger.Error(ex, "Redis miner stats stopped due to an error");
        }
        finally
        {
            acceptedSubscription?.Dispose();

            try
            {
                if(db != null)
                    await FlushAsync(CancellationToken.None);
            }
            catch(Exception ex)
            {
                logger.Warn(ex, "Final Redis miner-stats flush failed");
            }

            redis?.Dispose();
            logger.Info("Redis miner stats offline");
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if(db == null || redis?.IsConnected != true)
            return;

        var events = new List<MinerStatEvent>();
        while(queue.Reader.TryRead(out var evt))
            events.Add(evt);

        if(events.Count == 0)
            return;

        try
        {
            var aggregates = new Dictionary<(string PoolId, string Miner, string Worker), MinerStatAggregate>();

            foreach(var evt in events)
            {
                ct.ThrowIfCancellationRequested();
                var key = (evt.PoolId, evt.Miner, evt.Worker);

                if(evt.Type is MinerStatEventType.Connected or MinerStatEventType.Heartbeat or MinerStatEventType.Disconnected)
                {
                    // Preserve the lifecycle boundary. Flush counters queued before
                    // this connect/disconnect before changing global session state.
                    if(aggregates.Remove(key, out var pending))
                        await ApplyAggregateAsync(key, pending);

                    if(string.IsNullOrWhiteSpace(evt.SessionId))
                        continue;

                    if(evt.Type == MinerStatEventType.Disconnected)
                        await ApplyDisconnectAsync(evt);
                    else
                        await ApplyTouchAsync(evt);

                    continue;
                }

                if(!aggregates.TryGetValue(key, out var agg))
                {
                    agg = new MinerStatAggregate();
                    aggregates[key] = agg;
                }

                var unixMs = ToUnixMs(evt.Created);
                agg.FirstActivityUnixMs = Min(agg.FirstActivityUnixMs, unixMs);
                agg.LastActivityUnixMs = Max(agg.LastActivityUnixMs, unixMs);

                switch(evt.Type)
                {
                    case MinerStatEventType.Accepted:
                        agg.Accepted++;
                        agg.LastShareUnixMs = Max(agg.LastShareUnixMs, unixMs);
                        break;

                    case MinerStatEventType.Rejected:
                        agg.Rejected++;
                        agg.LastRejectUnixMs = Max(agg.LastRejectUnixMs, unixMs);
                        break;

                    case MinerStatEventType.Stale:
                        agg.Stale++;
                        agg.LastStaleUnixMs = Max(agg.LastStaleUnixMs, unixMs);
                        break;

                    case MinerStatEventType.Retarget:
                        agg.Retargets++;
                        agg.LastRetargetUnixMs = Max(agg.LastRetargetUnixMs, unixMs);
                        break;
                }

                if(evt.Difficulty.HasValue)
                    agg.CurrentDifficulty = evt.Difficulty;
            }

            foreach(var pair in aggregates)
            {
                ct.ThrowIfCancellationRequested();
                await ApplyAggregateAsync(pair.Key, pair.Value);
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch(Exception ex)
        {
            logger.Warn(ex, () => $"Unable to flush {events.Count} Redis miner-stat event(s)");
        }
    }

    private async Task ApplyTouchAsync(MinerStatEvent evt)
    {
        var walletHash = Hash(evt.Miner);
        var workerHash = Hash(evt.Worker);
        var now = ToUnixMs(evt.Created);
        var expiresAt = now + SessionTimeoutMs;

        await db.ScriptEvaluateAsync(
            TouchSessionScript,
            new RedisKey[]
            {
                WorkerSessionsKey(evt.PoolId, walletHash, workerHash),
                WorkerKey(evt.PoolId, walletHash, workerHash),
                WorkersKey(evt.PoolId, walletHash)
            },
            new RedisValue[]
            {
                now,
                expiresAt,
                evt.SessionId,
                evt.Worker,
                workerHash,
                LiveRetentionMs,
                SessionKeyTtlMs
            });
    }

    private async Task ApplyDisconnectAsync(MinerStatEvent evt)
    {
        var walletHash = Hash(evt.Miner);
        var workerHash = Hash(evt.Worker);
        var now = ToUnixMs(evt.Created);

        var result = await db.ScriptEvaluateAsync(
            DisconnectSessionScript,
            new RedisKey[]
            {
                WorkerSessionsKey(evt.PoolId, walletHash, workerHash),
                WorkerKey(evt.PoolId, walletHash, workerHash),
                WorkersKey(evt.PoolId, walletHash)
            },
            new RedisValue[]
            {
                now,
                evt.SessionId,
                evt.Worker,
                workerHash,
                ClosedRetentionMs,
                LiveRetentionMs,
                SessionKeyTtlMs
            });

        if(result.ToString() == "0")
            logger.Debug(() => $"Closed final Redis session for {evt.PoolId}/{evt.Miner}.{evt.Worker}");
    }

    private async Task ApplyAggregateAsync((string PoolId, string Miner, string Worker) key, MinerStatAggregate agg)
    {
        var (poolId, miner, worker) = key;
        var walletHash = Hash(miner);
        var workerHash = Hash(worker);
        var now = ToUnixMs(DateTime.UtcNow);

        await db.ScriptEvaluateAsync(
            UpdateWorkerScript,
            new RedisKey[]
            {
                WorkerSessionsKey(poolId, walletHash, workerHash),
                WorkerKey(poolId, walletHash, workerHash),
                WorkersKey(poolId, walletHash)
            },
            new RedisValue[]
            {
                now,
                workerHash,
                worker,
                agg.Accepted,
                agg.Rejected,
                agg.Stale,
                agg.Retargets,
                agg.LastActivityUnixMs ?? 0,
                agg.LastShareUnixMs ?? 0,
                agg.LastRejectUnixMs ?? 0,
                agg.LastStaleUnixMs ?? 0,
                agg.LastRetargetUnixMs ?? 0,
                agg.CurrentDifficulty?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
                LiveRetentionMs,
                ClosedRetentionMs
            });
    }

    public async Task<MinerShareStatsSnapshot> GetStatsAsync(string poolId, string address, CancellationToken ct)
    {
        if(!Enabled || db == null || string.IsNullOrWhiteSpace(poolId) || string.IsNullOrWhiteSpace(address))
            return null;

        var walletHash = Hash(address);
        var workersKey = WorkersKey(poolId, walletHash);

        try
        {
            var workerIds = await db.SetMembersAsync(workersKey).WaitAsync(ct);

            if(workerIds.Length == 0)
                return new MinerShareStatsSnapshot { Address = address };

            var now = ToUnixMs(DateTime.UtcNow);
            var activeWorkers = new List<(RedisValue WorkerId, string WorkerHash)>();
            var staleWorkerIds = new List<RedisValue>();

            foreach(var workerId in workerIds)
            {
                var workerHash = workerId.ToString();
                var sessionsKey = WorkerSessionsKey(poolId, walletHash, workerHash);

                await db.SortedSetRemoveRangeByScoreAsync(sessionsKey, double.NegativeInfinity, now);
                var count = await db.SortedSetLengthAsync(sessionsKey);

                if(count > 0)
                    activeWorkers.Add((workerId, workerHash));
                else
                    staleWorkerIds.Add(workerId);
            }

            if(staleWorkerIds.Count > 0)
            {
                await db.SetRemoveAsync(workersKey, staleWorkerIds.ToArray());
                if(await db.SetLengthAsync(workersKey) == 0)
                    await db.KeyDeleteAsync(workersKey);
            }

            if(activeWorkers.Count == 0)
                return new MinerShareStatsSnapshot { Address = address };

            var workerTasks = activeWorkers
                .Select(x => db.HashGetAllAsync(WorkerKey(poolId, walletHash, x.WorkerHash)))
                .ToArray();

            await Task.WhenAll(workerTasks).WaitAsync(ct);

            var workers = workerTasks
                .Select(x => MapWorker(x.Result))
                .Where(x => x != null)
                .OrderBy(x => x.Worker, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach(var worker in workers)
                worker.MinerWorker = worker.Worker == "default" ? address : $"{address}.{worker.Worker}";

            return new MinerShareStatsSnapshot
            {
                Address = address,
                Accepted = workers.Sum(x => x.Accepted),
                Rejected = workers.Sum(x => x.Rejected),
                Stale = workers.Sum(x => x.Stale),
                Retargets = workers.Sum(x => x.Retargets),
                LastShare = Latest(workers.Select(x => x.LastShare)),
                LastReject = Latest(workers.Select(x => x.LastReject)),
                LastStale = Latest(workers.Select(x => x.LastStale)),
                LastRetarget = Latest(workers.Select(x => x.LastRetarget)),
                Workers = workers
            };
        }
        catch(OperationCanceledException)
        {
            throw;
        }
        catch(Exception ex)
        {
            logger.Warn(ex, () => $"Unable to read Redis miner stats for pool {poolId}");
            return null;
        }
    }

    private static MinerWorkerShareStats MapWorker(HashEntry[] values)
    {
        if(values == null || values.Length == 0)
            return null;

        return new MinerWorkerShareStats
        {
            Worker = ReadString(values, "worker") ?? "default",
            Accepted = ReadLong(values, "accepted"),
            Rejected = ReadLong(values, "rejected"),
            Stale = ReadLong(values, "stale"),
            Retargets = ReadLong(values, "retargets"),
            CurrentDifficulty = ReadDouble(values, "currentDifficulty"),
            LastShare = ReadDate(values, "lastShare"),
            LastReject = ReadDate(values, "lastReject"),
            LastStale = ReadDate(values, "lastStale"),
            LastRetarget = ReadDate(values, "lastRetarget")
        };
    }

    private long SessionTimeoutMs =>
        (long) TimeSpan.FromSeconds(config?.SessionTimeoutSeconds is > 0 ? config.SessionTimeoutSeconds.Value : 120).TotalMilliseconds;

    private long SessionKeyTtlMs => Math.Max(SessionTimeoutMs * 2, 60_000);

    private long ClosedRetentionMs =>
        (long) TimeSpan.FromMinutes(config?.DisconnectedRetentionMinutes is > 0 ? config.DisconnectedRetentionMinutes.Value : 30).TotalMilliseconds;

    private long LiveRetentionMs =>
        (long) TimeSpan.FromDays(config?.RetentionDays is > 0 ? config.RetentionDays.Value : 30).TotalMilliseconds;

    private RedisKey WorkersKey(string poolId, string walletHash) => $"{Prefix}:{poolId}:{walletHash}:workers";
    private RedisKey WorkerKey(string poolId, string walletHash, string workerHash) => $"{Prefix}:{poolId}:{walletHash}:worker:{workerHash}";
    private RedisKey WorkerSessionsKey(string poolId, string walletHash, string workerHash) => $"{Prefix}:{poolId}:{walletHash}:worker:{workerHash}:sessions";

    private string Prefix => string.IsNullOrWhiteSpace(config?.KeyPrefix) ? "miningcore:minerstats" : config.KeyPrefix.TrimEnd(':');

    private static string NormalizeWorker(string worker) => string.IsNullOrWhiteSpace(worker) ? "default" : worker;

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static long ToUnixMs(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }

    private static long Max(long? current, long value) =>
        !current.HasValue || value > current.Value ? value : current.Value;

    private static long Min(long? current, long value) =>
        !current.HasValue || value < current.Value ? value : current.Value;

    private static DateTime? Latest(IEnumerable<DateTime?> values)
    {
        DateTime? result = null;

        foreach(var value in values)
        {
            if(value.HasValue && (!result.HasValue || value.Value > result.Value))
                result = value.Value;
        }

        return result;
    }

    private static string ReadString(HashEntry[] values, string name)
    {
        foreach(var entry in values)
        {
            if(entry.Name == name)
                return entry.Value.ToString();
        }

        return null;
    }

    private static long ReadLong(HashEntry[] values, string name)
    {
        var value = ReadString(values, name);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }

    private static double? ReadDouble(HashEntry[] values, string name)
    {
        var value = ReadString(values, name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static DateTime? ReadDate(HashEntry[] values, string name)
    {
        var unixMs = ReadLong(values, name);
        return unixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime : null;
    }
}
