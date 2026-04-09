namespace Eidet.Core.Domain;

public static class RepoIdNormalizer
{
    public static string Normalize(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            return "unknown";

        var normalized = directoryPath
            .TrimEnd('\\', '/')
            .Replace(':', '-')
            .Replace('\\', '-')
            .Replace('/', '-')
            .Replace('.', '-')
            .Replace('_', '-');
        return string.IsNullOrEmpty(normalized) ? "unknown" : normalized;
    }
}
