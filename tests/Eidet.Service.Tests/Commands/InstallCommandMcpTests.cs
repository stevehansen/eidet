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
    public async Task ClaudeDesktop_IsUnsupported_AndInstallNeverWritesItsConfig()
    {
        // One shared eidet process serves every desktop session, so it can't know the caller's repo.
        var desktop = McpClientRegistry.FindByName("claude-desktop")!;
        var before = desktop.ConfigPath is { } p && File.Exists(p) ? File.ReadAllText(p) : null;

        var (ok, detail) = await desktop.InstallAsync();

        Assert.NotNull(desktop.Unsupported);
        Assert.False(ok);
        Assert.Contains(desktop.Unsupported!, detail);
        Assert.Equal(before, desktop.ConfigPath is { } q && File.Exists(q) ? File.ReadAllText(q) : null);
    }

    [Fact]
    public void Registry_FindByName_IsCaseInsensitive()
    {
        Assert.NotNull(McpClientRegistry.FindByName("CLAUDE-CODE"));
        Assert.NotNull(McpClientRegistry.FindByName("Codex"));
        Assert.Null(McpClientRegistry.FindByName("does-not-exist"));
    }
}
