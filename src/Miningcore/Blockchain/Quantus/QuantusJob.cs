using System.Collections.Concurrent;
using System.Numerics;
using System.Globalization;

namespace Miningcore.Blockchain.Quantus;

public sealed class QuantusJob
{
    private readonly ConcurrentDictionary<string, byte> submissions =
        new(StringComparer.OrdinalIgnoreCase);

    public QuantusJob(QuantusMiningRequest request)
        : this(request, 0)
    {
    }

    public QuantusJob(
        QuantusMiningRequest request,
        long height)
    {
        ArgumentNullException.ThrowIfNull(request);

        if(height < 0)
            throw new ArgumentOutOfRangeException(
                nameof(height));

        if(string.IsNullOrWhiteSpace(request.JobId))
            throw new ArgumentException("Quantus job id is missing");

        if(string.IsNullOrWhiteSpace(request.MiningHash))
            throw new ArgumentException("Quantus mining hash is missing");

        var miningHash = QuantusEncoding.Hex(request.MiningHash, 32);

        if(miningHash.Length != 64)
            throw new ArgumentException(
                "Quantus mining hash must be 32 bytes");

        if(!BigInteger.TryParse(
            request.Difficulty, NumberStyles.None, CultureInfo.InvariantCulture,
            out var networkDifficulty) ||
            networkDifficulty <= BigInteger.Zero || networkDifficulty > QuantusPow.MaxTarget)
        {
            throw new ArgumentException(
                $"Invalid Quantus network difficulty: {request.Difficulty}");
        }

        UpstreamJobId = request.JobId;
        MiningHash = miningHash;
        NetworkDifficulty = networkDifficulty;
        Height = height;
        Created = DateTimeOffset.UtcNow;
    }

    public string UpstreamJobId { get; }

    public string MiningHash { get; }

    public BigInteger NetworkDifficulty { get; }

    public long Height { get; }

    public DateTimeOffset Created { get; }

    public QuantusShareResult ProcessShare(
        string work,
        BigInteger assignedDifficulty)
    {
        if(assignedDifficulty <= BigInteger.Zero || assignedDifficulty > QuantusPow.MaxTarget)
            return QuantusShareResult.Rejected(
                "invalid share difficulty");

        string normalizedWork;

        try
        {
            normalizedWork = NormalizeHex(work);
        }
        catch
        {
            return QuantusShareResult.Rejected(
                "invalid work encoding");
        }

        if(normalizedWork.Length != 128)
            return QuantusShareResult.Rejected(
                "Quantus work must be exactly 64 bytes");

        try
        {
            _ = Convert.FromHexString(normalizedWork);
        }
        catch
        {
            return QuantusShareResult.Rejected(
                "invalid work hex");
        }

        if(normalizedWork.All(c => c == '0'))
            return QuantusShareResult.Rejected("zero nonce is invalid in Quantus consensus");

        if(submissions.ContainsKey(normalizedWork))
            return QuantusShareResult.Rejected(
                "duplicate share");

        try
        {
            var shareTarget =
                QuantusPow.MaxTarget / assignedDifficulty;

            var networkTarget =
                QuantusPow.MaxTarget / NetworkDifficulty;

            var hashValue =
                QuantusPow.ComputeHashValue(
                    MiningHash,
                    normalizedWork);

            var achievedDifficulty =
                hashValue.IsZero
                    ? QuantusPow.MaxTarget
                    : QuantusPow.MaxTarget / hashValue;

            // Quantus consensus uses strict hash < target.
            var validShare =
                hashValue < shareTarget;

            var blockCandidate = hashValue < networkTarget;

            if(!validShare && !blockCandidate)
                return QuantusShareResult.Rejected(
                    "low difficulty share",
                    achievedDifficulty,
                    hashValue);

            if(!submissions.TryAdd(normalizedWork, 0))
                return QuantusShareResult.Rejected("duplicate share");

            return QuantusShareResult.AcceptedShare(
                achievedDifficulty,
                hashValue,
                blockCandidate);
        }
        catch(Exception ex)
        {
            return QuantusShareResult.Rejected(
                $"QPoW validation failed: {ex.Message}");
        }
    }

    private static string NormalizeHex(string value)
    {
        if(string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("hex value is empty");

        value = value.Trim();

        if(value.StartsWith(
            "0x",
            StringComparison.OrdinalIgnoreCase))
            value = value[2..];

        return value.ToLowerInvariant();
    }
}

public sealed record QuantusShareResult(
    bool Accepted,
    bool BlockCandidate,
    BigInteger AchievedDifficulty,
    BigInteger HashValue,
    string Error)
{
    public static QuantusShareResult AcceptedShare(
        BigInteger achievedDifficulty,
        BigInteger hashValue,
        bool blockCandidate)
    {
        return new QuantusShareResult(
            true,
            blockCandidate,
            achievedDifficulty,
            hashValue,
            null);
    }

    public static QuantusShareResult Rejected(
        string error,
        BigInteger achievedDifficulty = default,
        BigInteger hashValue = default)
    {
        return new QuantusShareResult(
            false,
            false,
            achievedDifficulty,
            hashValue,
            error);
    }
}
