using Eidet.Core.Domain;
using Eidet.Core.Intake.Git;
using Eidet.Core.Services;
using Eidet.Core.Tests.Services;

namespace Eidet.Core.Tests.Intake.Git;

/// <summary>
/// End-to-end <c>IngestGitAsync</c> pipeline — gate → mine → secret-skip → dedup → watermark —
/// over <see cref="InMemoryGitHistorySource"/> fixtures and the in-memory store. Zero subprocess.
/// </summary>
public class IntakeServiceGitTests
{
    private const string Repo = "test-repo";

    private static (IntakeService Service, InMemoryEidetStore Store) Build(InMemoryGitHistorySource git)
    {
        var store = new InMemoryEidetStore();
        var service = new IntakeService(store, [new GitHistoryExtractor(git)], new MemoryService(store));
        return (service, store);
    }

    private static Task<List<MemoryEntry>> StoredAsync(InMemoryEidetStore store) =>
        store.BrowseAsync(Repo, 0, 100);

    [Fact]
    public async Task FixCommit_StoresProcedure_WithIntakeProvenanceAndShaTag()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("a1b2c3", "fix: null deref in RecallScorer when tags empty",
                files: ["src/RecallScorer.cs"], hunk: "- if(tags.Count>0)\n+ if(tags is {Count:>0})");
        var (service, store) = Build(git);

        var result = await service.IngestGitAsync(Repo, "/x");

        Assert.Equal(1, result.NewCount);
        Assert.Contains("commit:a1b2c3", result.Items[0].Tags);

        var entry = Assert.Single(await StoredAsync(store));
        Assert.Equal(MemoryType.Procedure, entry.Type);
        Assert.Equal(MemoryProvenance.Intake, entry.Provenance);
        Assert.StartsWith($"memories/{Repo}/procedure/", entry.Id);
        Assert.Contains("git-intake", entry.Tags);
        Assert.DoesNotContain("if(tags is {Count:>0})", entry.Content);
    }

    [Fact]
    public async Task Rerun_OverSameCommits_DedupsAsDuplicate()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("s0", "fix: base commit below the explicit lower bound", files: ["src/Base.cs"])
            .AddCommit("a1b2c3", "fix: null deref in RecallScorer when tags empty", files: ["src/RecallScorer.cs"]);
        var (service, store) = Build(git);

        var first = await service.IngestGitAsync(Repo, "/x", new GitIntakeOptions(Since: "s0"));
        var second = await service.IngestGitAsync(Repo, "/x", new GitIntakeOptions(Since: "s0"));

        Assert.Equal(1, first.NewCount);
        Assert.Equal(0, second.NewCount);
        var item = Assert.Single(second.Items);
        Assert.True(item.WasSkipped);
        Assert.Equal("duplicate", item.SkipReason);
        Assert.Single(await StoredAsync(store));
    }

    [Fact]
    public async Task Watermark_AdvancesToTip_AndBoundsTheNextRun()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("c1", "fix: first bug in the intake pipeline", files: ["src/A.cs"]);
        var (service, store) = Build(git);

        var first = await service.IngestGitAsync(Repo, "/x");
        Assert.Equal(1, first.NewCount);
        Assert.Equal("c1", await store.GetGitIntakeWatermarkAsync(Repo));

        git.AddCommit("c2", "fix: second bug found after the first run", files: ["src/B.cs"]);
        var second = await service.IngestGitAsync(Repo, "/x");

        // Only the new commit is examined — c1 sits behind the watermark.
        var item = Assert.Single(second.Items);
        Assert.False(item.WasSkipped);
        Assert.Equal("commit c2", item.Source);
        Assert.Equal("c2", await store.GetGitIntakeWatermarkAsync(Repo));
    }

    [Fact]
    public async Task DryRun_PreviewsWithoutStoringOrAdvancingWatermark()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("d1", "fix: bug that dry-run should only preview", files: ["src/A.cs"]);
        var (service, store) = Build(git);

        var preview = await service.IngestGitAsync(Repo, "/x", dryRun: true);

        Assert.Equal(1, preview.NewCount);
        Assert.Empty(await StoredAsync(store));
        Assert.Null(await store.GetGitIntakeWatermarkAsync(Repo));

        // A real run afterwards stores it — proof the dry run persisted nothing.
        var real = await service.IngestGitAsync(Repo, "/x");
        Assert.Equal(1, real.NewCount);
        Assert.Single(await StoredAsync(store));
    }

    [Fact]
    public async Task SecretBearingCommit_SkippedNotAborted_AndRedacted()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("ok1234", "fix: clean commit stored despite the poisoned neighbour", files: ["src/Clean.cs"])
            // Newest commit carries the secret, so it is processed first — an aborting
            // pipeline would never reach the clean one.
            .AddCommit("bad567", "fix: rotate credentials", "New key AKIAIOSFODNN7EXAMPLE now active.",
                files: ["src/Config.cs"]);
        var (service, store) = Build(git);

        var result = await service.IngestGitAsync(Repo, "/x");

        Assert.Equal(1, result.NewCount);
        Assert.Equal(1, result.SkippedCount);

        var skipped = Assert.Single(result.Items, i => i.WasSkipped);
        Assert.StartsWith("secret-scan:", skipped.SkipReason);
        Assert.Equal("", skipped.Content); // caught secrets never ride out through the result

        var entry = Assert.Single(await StoredAsync(store));
        Assert.Contains("commit:ok1234", entry.Tags);
    }

    [Fact]
    public async Task UnavailableSource_ReportsNotARepo_InsteadOfSilentZero()
    {
        var (service, store) = Build(new InMemoryGitHistorySource { IsAvailable = false });

        var result = await service.IngestGitAsync(Repo, "/x");

        Assert.Equal(0, result.NewCount);
        var item = Assert.Single(result.Items);
        Assert.True(item.WasSkipped);
        Assert.Contains("not a git repository", item.SkipReason);
        Assert.Empty(await StoredAsync(store));
        Assert.Null(await store.GetGitIntakeWatermarkAsync(Repo));
    }

    [Fact]
    public async Task ExplicitSince_OverridesWatermark()
    {
        var git = new InMemoryGitHistorySource()
            .AddCommit("w1", "fix: commit processed by the first run", files: ["src/A.cs"])
            .AddCommit("w2", "fix: commit the explicit since should re-expose", files: ["src/B.cs"]);
        var (service, _) = Build(git);

        await service.IngestGitAsync(Repo, "/x"); // watermark → w2

        var rerun = await service.IngestGitAsync(Repo, "/x", new GitIntakeOptions(Since: "w1"));

        // Explicit Since wins over the watermark: w2 is re-examined (and dedups).
        var item = Assert.Single(rerun.Items);
        Assert.Equal("commit w2", item.Source);
        Assert.Equal("duplicate", item.SkipReason);
    }
}
