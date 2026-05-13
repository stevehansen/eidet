using Eidet.Service.Commands;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tests.Commands;

public class InstallCommandMcpTests
{
    [Fact]
    public async Task ConfigureMcpClientsAsync_DoesNotThrow()
    {
        // Walks every registered client on this machine. Result depends on
        // the host environment; the contract is just "doesn't blow up".
        var result = await InstallCommand.ConfigureMcpClientsAsync(CancellationToken.None);
        _ = result;
    }

    [Fact]
    public void Registry_HasKnownClients()
    {
        var names = McpClientRegistry.All.Select(c => c.Name).ToHashSet();
        Assert.Contains("claude-code", names);
        Assert.Contains("claude-desktop", names);
        Assert.Contains("codex", names);
        Assert.Contains("gemini", names);
    }

    [Fact]
    public void Registry_FindByName_IsCaseInsensitive()
    {
        Assert.NotNull(McpClientRegistry.FindByName("CLAUDE-CODE"));
        Assert.NotNull(McpClientRegistry.FindByName("Codex"));
        Assert.Null(McpClientRegistry.FindByName("does-not-exist"));
    }
}
