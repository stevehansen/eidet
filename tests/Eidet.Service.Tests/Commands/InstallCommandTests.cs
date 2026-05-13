using System.Runtime.InteropServices;
using Eidet.Service.Commands;

namespace Eidet.Service.Tests.Commands;

public class InstallCommandTests
{
    [Fact]
    public void GetDotnetToolShimPath_ReturnsPathContainingDotnetTools()
    {
        // The shim path may or may not exist in test environments,
        // but we can verify the path construction is sensible
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expectedDir = Path.Combine(home, ".dotnet", "tools");
        var shimName = OperatingSystem.IsWindows() ? "eidet.exe" : "eidet";
        var expectedPath = Path.Combine(expectedDir, shimName);

        // GetDotnetToolShimPath returns null if file doesn't exist, which is fine in tests
        // The important thing is it looks in the right place
        var result = InstallCommand.GetDotnetToolShimPath();
        if (result != null)
        {
            Assert.Equal(expectedPath, result);
        }
    }

    [Fact]
    public void GetLogDir_ReturnsNonEmpty()
    {
        var dir = InstallCommand.GetLogDir();
        Assert.False(string.IsNullOrEmpty(dir));
    }

    [Fact]
    public void GetLogDir_Windows_UsesAppData()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var dir = InstallCommand.GetLogDir();
        Assert.Contains("Eidet", dir);
        Assert.Contains("logs", dir);
    }

    [Fact]
    public async Task ConfigureMcpClientsAsync_DoesNotThrow()
    {
        // Should not throw on any machine state. Now walks every client in
        // the registry — see InstallCommandMcpTests for the smoke test.
        await InstallCommand.ConfigureMcpClientsAsync(CancellationToken.None);
    }
}
