using System.Security.Cryptography;
using Miningcore.Contracts;

namespace Miningcore.Crypto.Hashing.Algorithms;

/// <summary>
/// Sha-256 double round
/// </summary>
[Identifier("sha256d")]
public class Sha256D : IHashAlgorithm
{
    public void Digest(ReadOnlySpan<byte> data, Span<byte> result, params object[] extra)
    {
        Contract.Requires<ArgumentException>(result.Length >= 32);

        Span<byte> firstRound = stackalloc byte[32];

        SHA256.HashData(data, firstRound);
        SHA256.HashData(firstRound, result);
    }
}
