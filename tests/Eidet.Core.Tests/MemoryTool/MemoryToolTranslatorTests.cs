using System.Text.Json;
using Eidet.Core.MemoryTool;

namespace Eidet.Core.Tests.MemoryTool;

public class MemoryToolTranslatorTests
{
    private const string Repo = "P:/TestRepo";
    private const string NormalizedRepo = "P--TestRepo";

    private readonly InMemoryFileStore _files = new();

    private MemoryToolTranslator NewTranslator(IMemoryBridge? bridge = null, MemoryToolOptions? options = null) =>
        new(_files, Repo, bridge, options);

    private static MemoryPath P(string path) => MemoryPath.Of(path);

    // ─── Full command-set round-trip ──────────────────────────────────────

    [Fact]
    public async Task Create_View_StrReplace_View_RoundTripsByteExact()
    {
        var t = NewTranslator();

        var created = await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/plan.md"), "line one\nline two\n"));
        Assert.False(created.IsError);
        Assert.Equal("File created successfully at: /memories/plan.md", created.Text);

        var view1 = await t.ExecuteAsync(new MemoryCommand.View(P("/memories/plan.md")));
        Assert.False(view1.IsError);
        Assert.Contains("     1\tline one", view1.Text);
        Assert.Contains("     2\tline two", view1.Text);

        var replaced = await t.ExecuteAsync(new MemoryCommand.StrReplace(P("/memories/plan.md"), "line one", "LINE ONE"));
        Assert.False(replaced.IsError);
        Assert.Equal("The memory file /memories/plan.md has been edited.", replaced.Text);

        var view2 = await t.ExecuteAsync(new MemoryCommand.View(P("/memories/plan.md")));
        Assert.Contains("     1\tLINE ONE", view2.Text);

        // The blob is byte-exact — Claude re-reads exactly what it wrote.
        Assert.Equal("LINE ONE\nline two\n", await _files.ReadAsync(NormalizedRepo, "/memories/plan.md"));
    }

    [Fact]
    public async Task Insert_SplicesLines_PreservingTrailingNewline()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/list.md"), "alpha\ngamma\n"));

        var inserted = await t.ExecuteAsync(new MemoryCommand.Insert(P("/memories/list.md"), 1, "beta"));
        Assert.False(inserted.IsError);
        Assert.Equal("The file /memories/list.md has been edited.", inserted.Text);
        Assert.Equal("alpha\nbeta\ngamma\n", await _files.ReadAsync(NormalizedRepo, "/memories/list.md"));

        var atStart = await t.ExecuteAsync(new MemoryCommand.Insert(P("/memories/list.md"), 0, "zero"));
        Assert.False(atStart.IsError);
        Assert.Equal("zero\nalpha\nbeta\ngamma\n", await _files.ReadAsync(NormalizedRepo, "/memories/list.md"));
    }

    [Fact]
    public async Task Insert_WithoutTrailingNewline_AppendsAtEnd()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "only line"));

        var result = await t.ExecuteAsync(new MemoryCommand.Insert(P("/memories/x.md"), 1, "second"));
        Assert.False(result.IsError);
        Assert.Equal("only line\nsecond", await _files.ReadAsync(NormalizedRepo, "/memories/x.md"));
    }

    [Fact]
    public async Task Create_OnDirectoryPath_IsError()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/plans/a.md"), "a"));

        var result = await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/plans"), "shadow"));

        Assert.True(result.IsError);
        Assert.Contains("is a directory", result.Text);
        Assert.False(await _files.ExistsAsync(NormalizedRepo, "/memories/plans"));
    }

    [Fact]
    public async Task Create_OverwritesInPlace()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "first"));
        var second = await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "second"));

        Assert.False(second.IsError);
        Assert.Equal("second", await _files.ReadAsync(NormalizedRepo, "/memories/x.md"));
    }

    // ─── view ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task View_EmptyRoot_ReturnsListingHeaderNotError()
    {
        var result = await NewTranslator().ExecuteAsync(new MemoryCommand.View(P("/memories")));

        Assert.False(result.IsError);
        Assert.Equal("Here're the files and directories in /memories:", result.Text);
    }

    [Fact]
    public async Task View_Directory_ListsFilesAndSubdirectories()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/notes.md"), "n"));
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/plans/auth.md"), "a"));

        var result = await t.ExecuteAsync(new MemoryCommand.View(P("/memories")));

        Assert.False(result.IsError);
        Assert.Equal("Here're the files and directories in /memories:\nDIR\tplans/\nnotes.md", result.Text);
    }

    [Fact]
    public async Task View_MissingPath_IsError()
    {
        var result = await NewTranslator().ExecuteAsync(new MemoryCommand.View(P("/memories/nope.md")));

        Assert.True(result.IsError);
        Assert.Equal("The path /memories/nope.md does not exist. Please provide a valid path.", result.Text);
    }

    [Fact]
    public async Task View_RangeIsClampedToFile()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "one\ntwo\nthree\n"));

        var tail = await t.ExecuteAsync(new MemoryCommand.View(P("/memories/x.md"), (2, -1)));
        Assert.DoesNotContain("one", tail.Text);
        Assert.Contains("     2\ttwo", tail.Text);
        Assert.Contains("     3\tthree", tail.Text);

        var clamped = await t.ExecuteAsync(new MemoryCommand.View(P("/memories/x.md"), (-5, 99)));
        Assert.False(clamped.IsError);
        Assert.Contains("     1\tone", clamped.Text);
        Assert.Contains("     3\tthree", clamped.Text);
    }

    // ─── Malformed / traversal input ──────────────────────────────────────

    [Fact]
    public async Task Execute_ParsedTraversalPath_IsErrorAndStoreUntouched()
    {
        var cmd = MemoryCommand.Parse(JsonSerializer.Deserialize<JsonElement>(
            """{"command":"create","path":"/memories/../../etc/passwd","file_text":"pwned"}"""));

        var result = await NewTranslator().ExecuteAsync(cmd);

        Assert.True(result.IsError);
        Assert.Contains("traversal", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await _files.ListAsync(NormalizedRepo, "/memories"));
    }

    // ─── Secret gating ────────────────────────────────────────────────────

    private const string SecretContent = "aws key AKIAIOSFODNN7EXAMPLE leaked";

    [Fact]
    public async Task Create_WithSecret_DefaultRejects_IsErrorAndStoresNothing()
    {
        var result = await NewTranslator().ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), SecretContent));

        Assert.True(result.IsError);
        Assert.Contains("AWS access key", result.Text);
        Assert.Contains("Nothing was stored", result.Text);
        Assert.False(await _files.ExistsAsync(NormalizedRepo, "/memories/x.md"));
    }

    [Fact]
    public async Task StrReplace_IntroducingSecret_IsErrorAndFileUnchanged()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "the key is PLACEHOLDER"));

        var result = await t.ExecuteAsync(new MemoryCommand.StrReplace(
            P("/memories/x.md"), "PLACEHOLDER", "AKIAIOSFODNN7EXAMPLE"));

        Assert.True(result.IsError);
        Assert.Equal("the key is PLACEHOLDER", await _files.ReadAsync(NormalizedRepo, "/memories/x.md"));
    }

    [Fact]
    public async Task Create_WithSecret_RedactPolicy_WritesRedactedAndReportsIt()
    {
        var t = NewTranslator(options: new MemoryToolOptions { Secrets = SecretPolicy.Redact });

        var result = await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), SecretContent));

        Assert.False(result.IsError);
        Assert.Contains("redacted", result.Text);
        var stored = await _files.ReadAsync(NormalizedRepo, "/memories/x.md");
        Assert.Contains("[REDACTED:AWS access key]", stored);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", stored);
    }

    // ─── Size cap ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_OverMaxFileBytes_IsErrorAndStoresNothing()
    {
        var t = NewTranslator(options: new MemoryToolOptions { MaxFileBytes = 16 });

        var result = await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/big.md"), new string('x', 17)));

        Assert.True(result.IsError);
        Assert.Contains("maximum size", result.Text);
        Assert.False(await _files.ExistsAsync(NormalizedRepo, "/memories/big.md"));
    }

    [Fact]
    public async Task Insert_GrowingPastCap_IsErrorAndFileUnchanged()
    {
        var t = NewTranslator(options: new MemoryToolOptions { MaxFileBytes = 16 });
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "0123456789"));

        var result = await t.ExecuteAsync(new MemoryCommand.Insert(P("/memories/x.md"), 1, "0123456789"));

        Assert.True(result.IsError);
        Assert.Equal("0123456789", await _files.ReadAsync(NormalizedRepo, "/memories/x.md"));
    }

    // ─── str_replace occurrence rules ─────────────────────────────────────

    [Fact]
    public async Task StrReplace_NoOccurrence_IsError()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "hello"));

        var result = await t.ExecuteAsync(new MemoryCommand.StrReplace(P("/memories/x.md"), "absent", "y"));

        Assert.True(result.IsError);
        Assert.Equal("No replacement was performed, old_str `absent` did not appear verbatim in /memories/x.md.", result.Text);
    }

    [Fact]
    public async Task StrReplace_MultipleOccurrences_IsErrorListingLines()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "dup\nother\ndup\n"));

        var result = await t.ExecuteAsync(new MemoryCommand.StrReplace(P("/memories/x.md"), "dup", "y"));

        Assert.True(result.IsError);
        Assert.Contains("Multiple occurrences", result.Text);
        Assert.Contains("lines: 1, 3", result.Text);
    }

    [Fact]
    public async Task StrReplace_NullNewStr_DeletesText()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "keep REMOVE keep"));

        var result = await t.ExecuteAsync(new MemoryCommand.StrReplace(P("/memories/x.md"), " REMOVE", null));

        Assert.False(result.IsError);
        Assert.Equal("keep keep", await _files.ReadAsync(NormalizedRepo, "/memories/x.md"));
    }

    // ─── insert bounds ────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task Insert_OutOfBounds_IsError(int line)
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "one\ntwo\n"));

        var result = await t.ExecuteAsync(new MemoryCommand.Insert(P("/memories/x.md"), line, "new"));

        Assert.True(result.IsError);
        Assert.Contains($"Invalid `insert_line` parameter: {line}", result.Text);
        Assert.Contains("[0, 2]", result.Text);
    }

    // ─── delete ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_File_RemovesIt()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "gone soon"));

        var result = await t.ExecuteAsync(new MemoryCommand.Delete(P("/memories/x.md")));

        Assert.False(result.IsError);
        Assert.Equal("Successfully deleted /memories/x.md", result.Text);
        Assert.False(await _files.ExistsAsync(NormalizedRepo, "/memories/x.md"));
    }

    [Fact]
    public async Task Delete_Directory_RemovesAllChildren()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/plans/a.md"), "a"));
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/plans/deep/b.md"), "b"));
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/keep.md"), "keep"));

        var result = await t.ExecuteAsync(new MemoryCommand.Delete(P("/memories/plans")));

        Assert.False(result.IsError);
        Assert.Empty(await _files.ListAsync(NormalizedRepo, "/memories/plans"));
        Assert.True(await _files.ExistsAsync(NormalizedRepo, "/memories/keep.md"));
    }

    [Fact]
    public async Task Delete_Root_IsError()
    {
        var result = await NewTranslator().ExecuteAsync(new MemoryCommand.Delete(P("/memories")));

        Assert.True(result.IsError);
        Assert.Contains("cannot be deleted", result.Text);
    }

    [Fact]
    public async Task Delete_Missing_IsError()
    {
        var result = await NewTranslator().ExecuteAsync(new MemoryCommand.Delete(P("/memories/nope.md")));

        Assert.True(result.IsError);
        Assert.Equal("The path /memories/nope.md does not exist", result.Text);
    }

    // ─── rename ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Rename_File_MovesContent()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/a.md"), "payload"));

        var result = await t.ExecuteAsync(new MemoryCommand.Rename(P("/memories/a.md"), P("/memories/b.md")));

        Assert.False(result.IsError);
        Assert.Equal("Successfully renamed /memories/a.md to /memories/b.md", result.Text);
        Assert.False(await _files.ExistsAsync(NormalizedRepo, "/memories/a.md"));
        Assert.Equal("payload", await _files.ReadAsync(NormalizedRepo, "/memories/b.md"));
    }

    [Fact]
    public async Task Rename_Directory_MovesAllChildren()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/old/a.md"), "a"));
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/old/deep/b.md"), "b"));

        var result = await t.ExecuteAsync(new MemoryCommand.Rename(P("/memories/old"), P("/memories/new")));

        Assert.False(result.IsError);
        Assert.Equal("a", await _files.ReadAsync(NormalizedRepo, "/memories/new/a.md"));
        Assert.Equal("b", await _files.ReadAsync(NormalizedRepo, "/memories/new/deep/b.md"));
        Assert.Empty(await _files.ListAsync(NormalizedRepo, "/memories/old"));
    }

    [Fact]
    public async Task Rename_ToExistingDestination_IsError()
    {
        var t = NewTranslator();
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/a.md"), "a"));
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/b.md"), "b"));

        var result = await t.ExecuteAsync(new MemoryCommand.Rename(P("/memories/a.md"), P("/memories/b.md")));

        Assert.True(result.IsError);
        Assert.Equal("The destination /memories/b.md already exists", result.Text);
        Assert.Equal("b", await _files.ReadAsync(NormalizedRepo, "/memories/b.md"));
    }

    [Fact]
    public async Task Rename_MissingSource_IsError()
    {
        var result = await NewTranslator().ExecuteAsync(
            new MemoryCommand.Rename(P("/memories/nope.md"), P("/memories/x.md")));

        Assert.True(result.IsError);
        Assert.Equal("The path /memories/nope.md does not exist", result.Text);
    }

    [Fact]
    public async Task Rename_Root_IsError()
    {
        var result = await NewTranslator().ExecuteAsync(
            new MemoryCommand.Rename(P("/memories"), P("/memories/sub")));

        Assert.True(result.IsError);
        Assert.Contains("cannot be renamed", result.Text);
    }

    // ─── Repo isolation ───────────────────────────────────────────────────

    [Fact]
    public async Task Translators_AreRepoIsolated()
    {
        var t1 = new MemoryToolTranslator(_files, "P:/RepoOne");
        var t2 = new MemoryToolTranslator(_files, "P:/RepoTwo");
        await t1.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "repo one data"));

        var result = await t2.ExecuteAsync(new MemoryCommand.View(P("/memories/x.md")));

        Assert.True(result.IsError);
    }

    // ─── Bridge (off by default, opt-in shadow) ───────────────────────────

    [Fact]
    public async Task BridgeOffByDefault_WritesStayBlobOnly_AndRecallFindsNothing()
    {
        var t = NewTranslator(); // NullMemoryBridge default
        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "some scratch content"));

        var recall = await t.ExecuteAsync(new MemoryCommand.View(P("/memories/.recall/scratch")));

        Assert.False(recall.IsError);
        Assert.Equal("No recall results for \"scratch\".", recall.Text);
    }

    [Fact]
    public async Task Bridge_ReceivesPromotionsOnEveryContentWrite()
    {
        var bridge = new FakeBridge();
        var t = NewTranslator(bridge);

        await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "v1"));
        await t.ExecuteAsync(new MemoryCommand.StrReplace(P("/memories/x.md"), "v1", "v2"));
        await t.ExecuteAsync(new MemoryCommand.Insert(P("/memories/x.md"), 1, "v3"));

        Assert.Equal(["v1", "v2", "v2\nv3"], bridge.Promoted.Select(p => p.Content));
        Assert.All(bridge.Promoted, p => Assert.Equal("/memories/x.md", p.Path));
    }

    [Fact]
    public async Task Bridge_Recall_RendersHitsViaReservedPath()
    {
        var bridge = new FakeBridge { RecallHits = [("/memories/x.md", "raven config notes")] };
        var t = NewTranslator(bridge);

        var result = await t.ExecuteAsync(new MemoryCommand.View(P("/memories/.recall/raven config")));

        Assert.False(result.IsError);
        Assert.Equal("Recall results for \"raven config\":\n- /memories/x.md: raven config notes", result.Text);
        Assert.Equal("raven config", bridge.LastQuery);
    }

    [Fact]
    public async Task Bridge_Failure_NeverFailsTheWrite()
    {
        var t = NewTranslator(new FakeBridge { ThrowOnPromote = true });

        var result = await t.ExecuteAsync(new MemoryCommand.Create(P("/memories/x.md"), "still stored"));

        Assert.False(result.IsError);
        Assert.Equal("still stored", await _files.ReadAsync(NormalizedRepo, "/memories/x.md"));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("rename")]
    public async Task RecallSubtree_IsReadOnly(string kind)
    {
        var t = NewTranslator();
        MemoryCommand cmd = kind switch
        {
            "create" => new MemoryCommand.Create(P("/memories/.recall/x"), "nope"),
            "delete" => new MemoryCommand.Delete(P("/memories/.recall")),
            _ => new MemoryCommand.Rename(P("/memories/.recall"), P("/memories/elsewhere")),
        };

        var result = await t.ExecuteAsync(cmd);

        Assert.True(result.IsError);
        Assert.Contains("read-only", result.Text);
    }

    // ─── Never throws: storage faults become generic is_error ─────────────

    [Fact]
    public async Task StoreFault_BecomesGenericErrorResult_NotException()
    {
        var t = new MemoryToolTranslator(new ThrowingStore(), Repo);

        var result = await t.ExecuteAsync(new MemoryCommand.View(P("/memories/x.md")));

        Assert.True(result.IsError);
        Assert.Equal("The memory tool encountered an internal error. Please try again.", result.Text);
        Assert.DoesNotContain("boom", result.Text); // no exception detail leaks to the model
    }

    // ─── Test doubles ─────────────────────────────────────────────────────

    private sealed class FakeBridge : IMemoryBridge
    {
        public List<(string Path, string Content)> Promoted { get; } = [];
        public List<(string Path, string Snippet)> RecallHits { get; init; } = [];
        public string? LastQuery { get; private set; }
        public bool ThrowOnPromote { get; init; }

        public Task PromoteAsync(string repoId, string path, string content, CancellationToken ct = default)
        {
            if (ThrowOnPromote) throw new InvalidOperationException("bridge down");
            Promoted.Add((path, content));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<(string Path, string Snippet)>> RecallAsync(
            string repoId, string q, int limit, CancellationToken ct = default)
        {
            LastQuery = q;
            return Task.FromResult<IReadOnlyList<(string, string)>>(RecallHits);
        }
    }

    private sealed class ThrowingStore : IMemoryFileStore
    {
        public Task<bool> ExistsAsync(string repoId, string path, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<string?> ReadAsync(string repoId, string path, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task WriteAsync(string repoId, string path, string content, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task DeleteAsync(string repoId, string path, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task MoveAsync(string repoId, string oldPath, string newPath, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<string>> ListAsync(string repoId, string dir, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    }
}
