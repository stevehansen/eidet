using Eidet.Core.Domain;
using Eidet.Core.Intake.Extractors;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Intake;

/// <summary>
/// End-to-end interop intake (#66): AGENTS.md rides the default pass; the Claude Code
/// memory import is its own opt-in verb. Both flow through the shared sink, so the
/// per-candidate write gate covers them.
/// </summary>
public class IntakeServiceInteropTests
{
    private const string Repo = "test-repo";

    [Fact]
    public async Task DefaultIntake_IngestsAgentsMd_AndScansItsCandidates()
    {
        using var dir = new TempDirectory();
        dir.WriteFile("AGENTS.md",
            "## Conventions\nEvery tool handler is registered once in the dispatcher factory.\n" +
            "## Leaked Secret\nDeploy uses key AKIAIOSFODNN7EXAMPLE for the bucket, do not rotate.");
        var store = new InMemoryEidetStore();
        var service = new IntakeService(store, [new AgentsMdExtractor()], new MemoryService(store));

        var result = await service.IngestAsync(Repo, dir.Path);

        Assert.Equal(1, result.NewCount);
        var skipped = Assert.Single(result.Items, i => i.WasSkipped);
        Assert.StartsWith("secret-scan:", skipped.SkipReason);
        Assert.Equal("", skipped.Content);

        var entry = Assert.Single(await store.BrowseAsync(Repo, 0, 10));
        Assert.Contains("dispatcher factory", entry.Content);
        Assert.Equal(MemoryProvenance.Intake, entry.Provenance);
    }

    [Fact]
    public async Task IngestClaudeMemory_ImportsProjectMemory()
    {
        using var home = new TempDirectory();
        using var project = new TempDirectory();
        var slug = RepoIdNormalizer.Normalize(project.Path);
        home.WriteFile(Path.Combine("projects", slug, "memory", "MEMORY.md"),
            "## Scheduler\nOverdue tasks run within 30 seconds of service startup.");
        var store = new InMemoryEidetStore();
        var service = new IntakeService(store,
            [new ClaudeCodeMemoryExtractor(home.Path)], new MemoryService(store));

        var result = await service.IngestClaudeMemoryAsync(Repo, project.Path);

        Assert.Equal(1, result.NewCount);
        var entry = Assert.Single(await store.BrowseAsync(Repo, 0, 10));
        Assert.Contains("claude-code", entry.Tags);
    }

    [Fact]
    public async Task IngestClaudeMemory_NoMemoryDir_ReportsReasonInsteadOfSilentZero()
    {
        using var home = new TempDirectory();
        using var project = new TempDirectory();
        var store = new InMemoryEidetStore();
        var service = new IntakeService(store,
            [new ClaudeCodeMemoryExtractor(home.Path)], new MemoryService(store));

        var result = await service.IngestClaudeMemoryAsync(Repo, project.Path);

        Assert.Equal(0, result.NewCount);
        var item = Assert.Single(result.Items);
        Assert.True(item.WasSkipped);
        Assert.Contains("no Claude Code memory directory", item.SkipReason);
    }

    [Fact]
    public async Task IngestClaudeMemory_DryRun_StoresNothing()
    {
        using var home = new TempDirectory();
        using var project = new TempDirectory();
        var slug = RepoIdNormalizer.Normalize(project.Path);
        home.WriteFile(Path.Combine("projects", slug, "memory", "MEMORY.md"),
            "## Scheduler\nOverdue tasks run within 30 seconds of service startup.");
        var store = new InMemoryEidetStore();
        var service = new IntakeService(store,
            [new ClaudeCodeMemoryExtractor(home.Path)], new MemoryService(store));

        var preview = await service.IngestClaudeMemoryAsync(Repo, project.Path, dryRun: true);

        Assert.Equal(1, preview.NewCount);
        Assert.Empty(await store.BrowseAsync(Repo, 0, 10));
    }

    [Fact]
    public async Task DefaultIntake_DoesNotTouchClaudeMemory_WithoutOptIn()
    {
        using var home = new TempDirectory();
        using var project = new TempDirectory();
        var slug = RepoIdNormalizer.Normalize(project.Path);
        home.WriteFile(Path.Combine("projects", slug, "memory", "MEMORY.md"),
            "## External\nThis lives outside the repo and must stay out of the default pass.");
        var store = new InMemoryEidetStore();
        var service = new IntakeService(store,
            [new ClaudeCodeMemoryExtractor(home.Path)], new MemoryService(store));

        var result = await service.IngestAsync(Repo, project.Path);

        Assert.Equal(0, result.NewCount);
        Assert.Empty(await store.BrowseAsync(Repo, 0, 10));
    }
}
