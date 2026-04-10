using System.Runtime.InteropServices;
using Eidet.Service.Commands;

namespace Eidet.Service.Tests.Commands;

public class InstallCommandTests
{
    [Fact]
    public void GetInstallDir_ReturnsNonEmpty()
    {
        var dir = InstallCommand.GetInstallDir();
        Assert.False(string.IsNullOrEmpty(dir));
    }

    [Fact]
    public void GetInstallDir_ContainsEidet()
    {
        var dir = InstallCommand.GetInstallDir().ToLowerInvariant();
        Assert.Contains("eidet", dir);
    }

    [Fact]
    public void GetLogDir_ReturnsNonEmpty()
    {
        var dir = InstallCommand.GetLogDir();
        Assert.False(string.IsNullOrEmpty(dir));
    }

    [Fact]
    public void GetInstallDir_Windows_UsesAppData()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        var dir = InstallCommand.GetInstallDir();
        Assert.Contains("Eidet", dir);
        Assert.Contains("bin", dir);
    }
}
