using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using Miningcore.Mining;
using System.Threading;

namespace Miningcore.Blockchain.Bitcoin;

public class BitcoinWorkerContext : WorkerContextBase
{
    /// <summary>
    /// Usually a wallet address
    /// </summary>
    public override string Miner { get; set; }

    /// <summary>
    /// Arbitrary worker identififer for miners using multiple rigs
    /// </summary>
    public override string Worker { get; set; }

    /// <summary>
    /// Unique value assigned per worker
    /// </summary>
    public string ExtraNonce1 { get; set; }

    /// <summary>
    /// Mask for version-rolling (Overt ASIC-Boost)
    /// </summary>
    public uint? VersionRollingMask { get; internal set; }

    /// <summary>
    /// Current N job(s) assigned to this worker
    /// </summary>
    public Queue<BitcoinJob> validJobs { get; private set; } = new();

    private readonly Dictionary<string, HashSet<double>> jobDifficulties =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Elektron local Stratum work.
    ///
    /// The underlying BitcoinJob is the cached GBT job. WorkId, BaseVersion
    /// and NTime describe the exact mining.notify sent to this worker.
    /// </summary>
    public sealed record ElektronWorkAssignment(
        string WorkId,
        BitcoinJob Job,
        uint BaseVersion,
        uint NTime,
        double Difficulty);

    private readonly Queue<string> elektronWorkOrder = new();

    private readonly Dictionary<string, ElektronWorkAssignment> elektronWorks =
        new(StringComparer.OrdinalIgnoreCase);

    // Set by GetJob() for the current share-processing async context.
    // AsyncLocal keeps concurrent submissions from the same worker isolated.
    private readonly AsyncLocal<uint?> elektronSubmitBaseVersion = new();

    private uint elektronWorkCounter;
    private uint elektronVersionCounter;
    private string elektronPreviousBlockHash;

    public uint? ElektronSubmitBaseVersion => elektronSubmitBaseVersion.Value;

    public virtual void AddJob(BitcoinJob job, int maxActiveJobs, bool clearPreviousBlockJobs = true,
        double? difficulty = null)
    {
        if(clearPreviousBlockJobs && validJobs.Count > 0)
        {
            var previousBlockHash = validJobs.Last().BlockTemplate?.PreviousBlockhash;
            var newPreviousBlockHash = job.BlockTemplate?.PreviousBlockhash;

            // New chain tip: previous-block work must no longer be accepted.
            if(!string.Equals(previousBlockHash, newPreviousBlockHash, StringComparison.OrdinalIgnoreCase))
            {
                validJobs.Clear();
                jobDifficulties.Clear();
            }
        }

        if(!validJobs.Contains(job))
            validJobs.Enqueue(job);

        // A Stratum job id can be re-announced after a vardiff change.
        // Keep every difficulty actually announced for that retained job.
        if(difficulty is > 0)
        {
            if(!jobDifficulties.TryGetValue(job.JobId, out var difficulties))
            {
                difficulties = new HashSet<double>();
                jobDifficulties[job.JobId] = difficulties;
            }

            difficulties.Add(difficulty.Value);
        }

        while(validJobs.Count > maxActiveJobs)
        {
            var removed = validJobs.Dequeue();

            if(!validJobs.Any(x =>
                string.Equals(x.JobId, removed.JobId, StringComparison.OrdinalIgnoreCase)))
            {
                jobDifficulties.Remove(removed.JobId);
            }
        }
    }

    /// <summary>
    /// Create fresh Elektron header work from an existing cached GBT job.
    ///
    /// No daemon RPC is performed here. Pool-controlled BIP320 bits 13..16
    /// rotate through all 16 states while the Stratum work-id remains unique.
    /// </summary>
    public ElektronWorkAssignment AddElektronWork(
        BitcoinJob job,
        int maxActiveJobs,
        double difficulty,
        uint nTime)
    {
        var previousBlockHash = job.BlockTemplate?.PreviousBlockhash ?? string.Empty;

        // New chain tip: discard all local work from the old block and restart
        // the 4-bit pool rotation at state zero. Keep the work-id counter
        // monotonic so delayed old submissions can never alias a new job.
        if(!string.Equals(elektronPreviousBlockHash, previousBlockHash,
            StringComparison.OrdinalIgnoreCase))
        {
            elektronWorks.Clear();
            elektronWorkOrder.Clear();
            elektronVersionCounter = 0;
            elektronPreviousBlockHash = previousBlockHash;
        }

        var rotationState = elektronVersionCounter++ & 0x0f;
        var poolBits = rotationState << 13;

        var baseVersion =
            (job.BlockTemplate.Version & ~ElektronJob.PoolVersionBitsMask) |
            (poolBits & ElektronJob.PoolVersionBitsMask);

        unchecked
        {
            elektronWorkCounter++;

            if(elektronWorkCounter == 0)
                elektronWorkCounter = 1;
        }

        // Eight hex characters keeps compatibility with ASIC firmware that
        // expects Bitcoin-style hexadecimal Stratum job identifiers.
        var workId = elektronWorkCounter.ToString("x8");

        var assignment = new ElektronWorkAssignment(
            workId,
            job,
            baseVersion,
            nTime,
            difficulty);

        elektronWorks[workId] = assignment;
        elektronWorkOrder.Enqueue(workId);

        // At a 500ms refresh this retains roughly two minutes of local work.
        // This is deliberately larger than normal manager maxActiveJobs because
        // the records are tiny and ASICs may return buffered results.
        var retention = Math.Max(maxActiveJobs, 256);

        while(elektronWorkOrder.Count > retention)
        {
            var removed = elektronWorkOrder.Dequeue();
            elektronWorks.Remove(removed);
        }

        return assignment;
    }

    public ElektronWorkAssignment GetElektronWork(string workId)
    {
        if(!string.IsNullOrEmpty(workId) &&
            elektronWorks.TryGetValue(workId, out var work))
        {
            return work;
        }

        return null;
    }

    public BitcoinJob GetJob(string jobId)
    {
        // Elektron submits the local Stratum work-id, not the underlying
        // Miningcore/GBT job-id.
        if(!string.IsNullOrEmpty(jobId) &&
            elektronWorks.TryGetValue(jobId, out var elektronWork))
        {
            elektronSubmitBaseVersion.Value = elektronWork.BaseVersion;
            return elektronWork.Job;
        }

        elektronSubmitBaseVersion.Value = null;

        return validJobs.ToArray().FirstOrDefault(x =>
            string.Equals(x.JobId, jobId, StringComparison.OrdinalIgnoreCase));
    }

    public double[] GetJobDifficulties(string jobId)
    {
        if(!string.IsNullOrEmpty(jobId) &&
            elektronWorks.TryGetValue(jobId, out var elektronWork))
        {
            return elektronWork.Difficulty > 0
                ? new[] { elektronWork.Difficulty }
                : Array.Empty<double>();
        }

        if(jobDifficulties.TryGetValue(jobId, out var difficulties))
            return difficulties.OrderByDescending(x => x).ToArray();

        return Array.Empty<double>();
    }

    public BitcoinJob[] GetJobsSnapshot()
    {
        return validJobs.ToArray();
    }
}
