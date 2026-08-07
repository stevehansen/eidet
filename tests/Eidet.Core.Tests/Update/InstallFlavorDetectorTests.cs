using Eidet.Core.Update;

namespace Eidet.Core.Tests.Update;

public class InstallFlavorDetectorTests
{
    [Theory]
    [InlineData("/home/steve/.dotnet/tools/.store/eidet/0.10.0/eidet/0.10.0/tools/net10.0/any/")]
    [InlineData(@"C:\Users\steve\.dotnet\tools\.store\eidet\0.10.0\eidet\0.10.0\tools\net10.0\any\")]
    [InlineData(@"C:\Users\steve\.dotnet\tools\")]
    [InlineData("/opt/tools/.store/eidet/0.10.0/")]
    public void Recognises_a_dotnet_tool_layout(string path)
    {
        Assert.True(InstallFlavorDetector.IsDotnetToolPath(path));
    }

    [Theory]
    [InlineData("/usr/local/bin/")]
    [InlineData(@"C:\Program Files\Eidet\")]
    [InlineData("/app/")]
    [InlineData("P:/Eidet/src/Eidet.Service/bin/Release/net10.0/")]
    public void Treats_anything_else_as_standalone(string path)
    {
        Assert.False(InstallFlavorDetector.IsDotnetToolPath(path));
        Assert.Equal(InstallFlavor.Standalone, InstallFlavorDetector.Detect(path));
    }

    [Fact]
    public void A_build_output_directory_is_not_mistaken_for_a_tool_install()
    {
        // Guessing wrong in this direction is the expensive one: it schedules a
        // `dotnet tool update` at 04:00 against something dotnet does not manage.
        Assert.Equal(InstallFlavor.Standalone,
            InstallFlavorDetector.Detect("P:/Eidet/src/Eidet.Service/bin/Release/net10.0/"));
    }
}
