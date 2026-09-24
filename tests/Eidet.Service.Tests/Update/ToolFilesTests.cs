using Eidet.Service.Update;

namespace Eidet.Service.Tests.Update;

/// <summary>
/// An update must not kill the AI sessions running <c>eidet mcp</c>: their open files are moved out
/// of <c>dotnet tool update</c>'s way instead. A handle opened with <see cref="FileShare.Delete"/>
/// stands in for the loader, which maps a running image the same way.
/// </summary>
public class ToolFilesTests : IDisposable
{
    private readonly string _tools = Directory.CreateTempSubdirectory("eidet-tools-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tools, recursive: true); } catch { }
    }

    private string Tool(string relative)
    {
        var path = Path.Combine(_tools, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relative);
        return path;
    }

    private static FileStream HoldOpen(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    [Fact]
    public void InUseFiles_AreMovedAside_TheRestStayForDotnet()
    {
        if (!OperatingSystem.IsWindows()) return; // only Windows refuses to delete open files

        var shim = Tool("eidet.exe");
        var dll = Tool(Path.Combine(".store", "eidet", "0.14.5", "eidet", "0.14.5", "tools", "net10.0", "any", "Eidet.Service.dll"));
        var manifest = Tool(Path.Combine(".store", "eidet", "0.14.5", "eidet", "0.14.5", "eidet.nuspec"));
        var otherTool = Tool("parley.exe");

        List<ToolFiles.Move> moves;
        using (HoldOpen(shim))
        using (HoldOpen(dll))
            moves = ToolFiles.MoveInUseAside(_tools);

        Assert.Equal(new HashSet<string> { shim, dll }, moves.Select(m => m.From).ToHashSet());
        Assert.All(moves, m => Assert.StartsWith(ToolFiles.LeftoversRoot(_tools), m.To));
        Assert.False(File.Exists(shim));
        Assert.True(File.Exists(manifest));
        Assert.True(File.Exists(otherTool));
    }

    [Fact]
    public void Restore_PutsMovedFilesBack()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dll = Tool(Path.Combine(".store", "eidet", "0.14.5", "Eidet.Core.dll"));
        List<ToolFiles.Move> moves;
        using (HoldOpen(dll))
            moves = ToolFiles.MoveInUseAside(_tools);

        ToolFiles.Restore(moves);

        Assert.Equal(Path.Combine(".store", "eidet", "0.14.5", "Eidet.Core.dll"), File.ReadAllText(dll));
    }

    [Fact]
    public void DeleteLeftovers_RemovesMovedCopiesOnceReleased()
    {
        if (!OperatingSystem.IsWindows()) return;

        var shim = Tool("eidet.exe");
        using (HoldOpen(shim))
            ToolFiles.MoveInUseAside(_tools);

        ToolFiles.DeleteLeftovers(_tools);

        Assert.False(Directory.Exists(ToolFiles.LeftoversRoot(_tools)));
    }

    [Fact]
    public void NothingInUse_MovesNothing()
    {
        Tool("eidet.exe");

        Assert.Empty(ToolFiles.MoveInUseAside(_tools));
    }
}
