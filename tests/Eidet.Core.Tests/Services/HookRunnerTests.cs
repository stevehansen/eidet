using Eidet.Core.Configuration;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class HookRunnerTests
{
    // ─── NullHookRunner ──────────────────────────────────────────

    [Fact]
    public async Task NullHookRunner_AlwaysAllows()
    {
        var result = await NullHookRunner.Instance.RunPreHooksAsync(
            HookEvent.PreStore, new HookContext { Event = "pre-store", Repo = "test" }, CancellationToken.None);
        Assert.True(result.Allowed);
    }

    [Fact]
    public void NullHookRunner_HasNoHooks()
    {
        Assert.False(NullHookRunner.Instance.HasHooks(HookEvent.PreStore));
        Assert.False(NullHookRunner.Instance.HasHooks(HookEvent.PostStore));
        Assert.False(NullHookRunner.Instance.HasHooks(HookEvent.PreRecall));
    }

    // ─── ParseCommand ────────────────────────────────────────────

    [Fact]
    public void ParseCommand_SimpleCommand()
    {
        var (file, args) = HookRunner.ParseCommand("python validate.py");
        Assert.Equal("python", file);
        Assert.Equal("validate.py", args);
    }

    [Fact]
    public void ParseCommand_NoArgs()
    {
        var (file, args) = HookRunner.ParseCommand("myvalidator");
        Assert.Equal("myvalidator", file);
        Assert.Equal("", args);
    }

    [Fact]
    public void ParseCommand_QuotedExecutable()
    {
        var (file, args) = HookRunner.ParseCommand("\"C:\\Program Files\\tool.exe\" --flag");
        Assert.Equal("C:\\Program Files\\tool.exe", file);
        Assert.Equal("--flag", args);
    }

    [Fact]
    public void ParseCommand_MultipleArgs()
    {
        var (file, args) = HookRunner.ParseCommand("node scripts/hook.js --verbose --repo test");
        Assert.Equal("node", file);
        Assert.Equal("scripts/hook.js --verbose --repo test", args);
    }

    // ─── HookRunner with config ──────────────────────────────────

    [Fact]
    public void HasHooks_ReturnsTrueWhenEnabled()
    {
        var config = new HooksConfig
        {
            PreStore = [new HookDefinition { Command = "echo test", Enabled = true }],
        };
        var runner = new HookRunner(config);
        Assert.True(runner.HasHooks(HookEvent.PreStore));
        Assert.False(runner.HasHooks(HookEvent.PostStore));
    }

    [Fact]
    public void HasHooks_ReturnsFalseWhenAllDisabled()
    {
        var config = new HooksConfig
        {
            PreStore = [new HookDefinition { Command = "echo test", Enabled = false }],
        };
        var runner = new HookRunner(config);
        Assert.False(runner.HasHooks(HookEvent.PreStore));
    }

    [Fact]
    public async Task RunPreHooks_NoHooks_ReturnsOk()
    {
        var runner = new HookRunner(new HooksConfig());
        var result = await runner.RunPreHooksAsync(HookEvent.PreStore,
            new HookContext { Event = "pre-store", Repo = "test" }, CancellationToken.None);
        Assert.True(result.Allowed);
    }

    // ─── HookEvent mapping ───────────────────────────────────────

    [Theory]
    [InlineData(HookEvent.PreStore)]
    [InlineData(HookEvent.PostStore)]
    [InlineData(HookEvent.PreRecall)]
    [InlineData(HookEvent.PostRecall)]
    [InlineData(HookEvent.PreForget)]
    [InlineData(HookEvent.PostForget)]
    public void AllHookEvents_MapToConfigLists(HookEvent evt)
    {
        var config = new HooksConfig();
        var runner = new HookRunner(config);
        // Should not throw — all events are handled
        Assert.False(runner.HasHooks(evt));
    }

    // ─── HookDefinition defaults ─────────────────────────────────

    [Fact]
    public void HookDefinition_HasSensibleDefaults()
    {
        var def = new HookDefinition();
        Assert.Equal("", def.Command);
        Assert.Equal(10, def.TimeoutSeconds);
        Assert.True(def.Enabled);
    }

    // ─── HooksConfig defaults ────────────────────────────────────

    [Fact]
    public void HooksConfig_DefaultsToEmptyLists()
    {
        var config = new HooksConfig();
        Assert.Empty(config.PreStore);
        Assert.Empty(config.PostStore);
        Assert.Empty(config.PreRecall);
        Assert.Empty(config.PostRecall);
        Assert.Empty(config.PreForget);
        Assert.Empty(config.PostForget);
    }

    // ─── HookContext ─────────────────────────────────────────────

    [Fact]
    public void HookContext_SetsProperties()
    {
        var ctx = new HookContext
        {
            Event = "pre-store",
            Repo = "P:\\MyProject",
            Data = new { content = "test" },
        };
        Assert.Equal("pre-store", ctx.Event);
        Assert.Equal("P:\\MyProject", ctx.Repo);
        Assert.NotNull(ctx.Data);
        Assert.True(ctx.Timestamp <= DateTime.UtcNow);
    }

    // ─── HookResult ──────────────────────────────────────────────

    [Fact]
    public void HookResult_Ok_IsAllowed()
    {
        var result = HookResult.Ok();
        Assert.True(result.Allowed);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void HookResult_Rejected_HasReason()
    {
        var result = HookResult.Rejected("bad content");
        Assert.False(result.Allowed);
        Assert.Equal("bad content", result.Reason);
    }
}
