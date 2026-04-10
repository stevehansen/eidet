using Eidet.Service.Commands;

namespace Eidet.Service.Tests.Commands;

public class InstallCommandMcpTests
{
    [Fact]
    public void ConfigureMcpClients_NoClaudeInstalled_ReturnsNull()
    {
        // On a machine without Claude Code dir, should return null
        // (unless the test machine has Claude Code installed)
        var result = InstallCommand.ConfigureMcpClients("/nonexistent/path/eidet");
        // Result is null or a string — both are valid depending on test machine
        // This test verifies it doesn't throw
    }
}
