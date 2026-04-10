using Eidet.Service.Commands;

namespace Eidet.Service.Tests.Commands;

public class DockerCommandTests
{
    [Fact]
    public void IsRunningInContainer_OnHost_ReturnsFalse()
    {
        // On a normal dev machine this should be false
        // (unless tests are running in Docker, in which case this test is expected to fail)
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
            return; // Skip in container

        Assert.False(DockerCommand.IsRunningInContainer());
    }
}
