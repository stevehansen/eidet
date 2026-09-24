using Microsoft.Win32.SafeHandles;

namespace Eidet.Service.Update;

/// <summary>
/// Lets <c>dotnet tool update</c> succeed on Windows while AI sessions keep running their
/// <c>eidet mcp</c> processes.
///
/// A running eidet keeps its program files open, and Windows won't delete open files, so the
/// update's uninstall of the old version fails on them. It will <em>move</em> them though, and the
/// process keeps running from the new location. So before updating, the files that are in use
/// are moved to <c>~/.dotnet/tools/.eidet-old/</c> (same volume, so a move is a rename); the rest
/// stays for dotnet to read and remove. When the update fails, the files go back. The moved copies
/// are deleted once their processes have exited (<see cref="DeleteLeftovers"/>).
///
/// A session left on the old version runs the code it has already loaded; an assembly it first
/// needs after the update is gone with the old install, so such a session should be restarted.
/// That is still strictly better than the previous behaviour of killing every session outright.
/// </summary>
internal static class ToolFiles
{
    public sealed record Move(string From, string To);

    private const string PackageId = "eidet";

    /// <summary>The global tool directory <c>dotnet tool update -g</c> installs into.</summary>
    public static string GlobalToolsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");

    /// <param name="toolsDir">The tool directory holding the <c>eidet</c> shim and <c>.store</c>.</param>
    public static List<Move> MoveInUseAside(string toolsDir)
    {
        var trash = Path.Combine(LeftoversRoot(toolsDir), Guid.NewGuid().ToString("N"));
        var store = Path.Combine(toolsDir, ".store", PackageId);
        var files = Directory.Exists(toolsDir)
            ? Directory.EnumerateFiles(toolsDir, $"{PackageId}*").ToList()
            : [];
        if (Directory.Exists(store)) files.AddRange(Directory.EnumerateFiles(store, "*", SearchOption.AllDirectories));

        var moves = new List<Move>();
        foreach (var file in files.Where(InUse))
        {
            var to = Path.Combine(trash, Path.GetRelativePath(toolsDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(file, to);
            moves.Add(new Move(file, to));
        }
        return moves;
    }

    /// <summary>Undoes <see cref="MoveInUseAside"/> after a failed update.</summary>
    public static void Restore(IEnumerable<Move> moves)
    {
        foreach (var m in moves)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(m.From)!);
            File.Move(m.To, m.From, overwrite: true);
        }
    }

    /// <summary>Deletes moved-aside files whose processes have exited; the rest waits for next time.</summary>
    public static void DeleteLeftovers(string toolsDir)
    {
        var root = LeftoversRoot(toolsDir);
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try { Directory.Delete(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static string LeftoversRoot(string toolsDir) => Path.Combine(toolsDir, ".eidet-old");

    /// <summary>Open elsewhere — an exclusive open fails with a sharing violation.</summary>
    private static bool InUse(string file)
    {
        try
        {
            using SafeFileHandle _ = File.OpenHandle(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // a running .exe can't be opened for writing at all
        }
    }
}
