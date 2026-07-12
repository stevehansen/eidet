using System.Security.Cryptography;
using System.Text;

namespace Eidet.Core.Domain;

/// <summary>
/// The version identity of a memory's content: lowercase-hex SHA256 over the UTF-8 content. Used as
/// the optimistic-concurrency token on the edit path (#65) — a caller that reads a memory, hashes what
/// it read, and passes that hash back with its edit can never silently clobber a concurrent edit.
/// </summary>
public static class ContentHash
{
    public static string Of(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? ""))).ToLowerInvariant();
}
