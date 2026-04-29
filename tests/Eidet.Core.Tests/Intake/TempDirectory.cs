namespace Eidet.Core.Tests.Intake;

/// <summary>
/// Disposable scratch directory under <see cref="Path.GetTempPath"/>. Auto-creates the
/// folder, gives helpers for staging files, and removes the tree on dispose. Per-test
/// isolation means parallel runs don't collide.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eidet-intake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string WriteFile(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
