using System.Security.Cryptography;
using System.Text;

namespace Eidet.Core.Domain;

public static class MemoryIdGenerator
{
    public static string Generate(string repoId, MemoryType type, string content, DateTime createdAt)
    {
        var input = content + createdAt.ToString("O");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var shortHash = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return $"memories/{repoId}/{type.ToString().ToLowerInvariant()}/{shortHash}";
    }
}
