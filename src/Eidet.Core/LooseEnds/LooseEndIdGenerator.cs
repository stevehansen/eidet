using System.Security.Cryptography;
using System.Text;

namespace Eidet.Core.LooseEnds;

/// <summary>
/// Deterministic Loose End document IDs: "looseends/{repoId}/{shortHash}".
/// shortHash = first 12 chars of SHA256(note + createdAt). No type segment — one kind.
/// </summary>
public static class LooseEndIdGenerator
{
    public static string Generate(string repoId, string note, DateTimeOffset createdAt)
    {
        var input = note + createdAt.ToString("O");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var shortHash = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return $"looseends/{repoId}/{shortHash}";
    }
}
