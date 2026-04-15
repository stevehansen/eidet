using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class LayerSyncServiceTests
{
    // ─── ContentEquals ────────────────────────────────────────────

    [Fact]
    public void ContentEquals_IdenticalEntries_ReturnsTrue()
    {
        var a = MakeEntry("test content", MemoryType.Insight, 0.5f, ["tag1"]);
        var b = MakeEntry("test content", MemoryType.Insight, 0.5f, ["tag1"]);
        Assert.True(LayerSyncService.ContentEquals(a, b));
    }

    [Fact]
    public void ContentEquals_DifferentContent_ReturnsFalse()
    {
        var a = MakeEntry("content A", MemoryType.Insight, 0.5f, ["tag1"]);
        var b = MakeEntry("content B", MemoryType.Insight, 0.5f, ["tag1"]);
        Assert.False(LayerSyncService.ContentEquals(a, b));
    }

    [Fact]
    public void ContentEquals_DifferentType_ReturnsFalse()
    {
        var a = MakeEntry("same content", MemoryType.Insight, 0.5f, ["tag1"]);
        var b = MakeEntry("same content", MemoryType.Procedure, 0.5f, ["tag1"]);
        Assert.False(LayerSyncService.ContentEquals(a, b));
    }

    [Fact]
    public void ContentEquals_DifferentImportance_ReturnsFalse()
    {
        var a = MakeEntry("same content", MemoryType.Insight, 0.5f, ["tag1"]);
        var b = MakeEntry("same content", MemoryType.Insight, 0.8f, ["tag1"]);
        Assert.False(LayerSyncService.ContentEquals(a, b));
    }

    [Fact]
    public void ContentEquals_DifferentTags_ReturnsFalse()
    {
        var a = MakeEntry("same content", MemoryType.Insight, 0.5f, ["tag1"]);
        var b = MakeEntry("same content", MemoryType.Insight, 0.5f, ["tag2"]);
        Assert.False(LayerSyncService.ContentEquals(a, b));
    }

    [Fact]
    public void ContentEquals_TagsInDifferentOrder_ReturnsTrue()
    {
        var a = MakeEntry("same content", MemoryType.Insight, 0.5f, ["alpha", "beta"]);
        var b = MakeEntry("same content", MemoryType.Insight, 0.5f, ["beta", "alpha"]);
        Assert.True(LayerSyncService.ContentEquals(a, b));
    }

    [Fact]
    public void ContentEquals_TagsCaseInsensitive_ReturnsTrue()
    {
        var a = MakeEntry("same content", MemoryType.Insight, 0.5f, ["Tag1"]);
        var b = MakeEntry("same content", MemoryType.Insight, 0.5f, ["tag1"]);
        Assert.True(LayerSyncService.ContentEquals(a, b));
    }

    [Fact]
    public void ContentEquals_DifferentOneLiner_ReturnsFalse()
    {
        var a = MakeEntry("same content", MemoryType.Insight, 0.5f, []);
        a.OneLiner = "one-liner A";
        var b = MakeEntry("same content", MemoryType.Insight, 0.5f, []);
        b.OneLiner = "one-liner B";
        Assert.False(LayerSyncService.ContentEquals(a, b));
    }

    [Fact]
    public void ContentEquals_DifferentSummary_ReturnsFalse()
    {
        var a = MakeEntry("same content", MemoryType.Insight, 0.5f, []);
        a.Summary = "summary A";
        var b = MakeEntry("same content", MemoryType.Insight, 0.5f, []);
        b.Summary = "summary B";
        Assert.False(LayerSyncService.ContentEquals(a, b));
    }

    [Fact]
    public void ContentEquals_IgnoresAccessCount()
    {
        var a = MakeEntry("same content", MemoryType.Insight, 0.5f, ["tag1"]);
        a.AccessCount = 5;
        var b = MakeEntry("same content", MemoryType.Insight, 0.5f, ["tag1"]);
        b.AccessCount = 0;
        Assert.True(LayerSyncService.ContentEquals(a, b));
    }

    // ─── Domain types ─────────────────────────────────────────────

    [Fact]
    public void SyncAction_HasExpectedValues()
    {
        Assert.Equal(0, (int)SyncAction.Unchanged);
        Assert.Equal(1, (int)SyncAction.Add);
        Assert.Equal(2, (int)SyncAction.Update);
        Assert.Equal(3, (int)SyncAction.Remove);
    }

    [Fact]
    public void LayerSyncPreview_Defaults()
    {
        var preview = new LayerSyncPreview();
        Assert.Equal("", preview.LayerId);
        Assert.Equal("", preview.PackName);
        Assert.Equal("", preview.PackVersion);
        Assert.Null(preview.CurrentVersion);
        Assert.Equal(0, preview.Added);
        Assert.Equal(0, preview.Updated);
        Assert.Equal(0, preview.Removed);
        Assert.Equal(0, preview.Unchanged);
        Assert.Empty(preview.Entries);
    }

    [Fact]
    public void LayerSyncResult_Defaults()
    {
        var result = new LayerSyncResult();
        Assert.Equal("", result.LayerId);
        Assert.Equal("", result.PackName);
        Assert.Equal("", result.PackVersion);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Unchanged);
        Assert.Equal(0, result.StaleKept);
    }

    [Fact]
    public void SyncEntryPreview_Defaults()
    {
        var entry = new SyncEntryPreview();
        Assert.Equal("", entry.Id);
        Assert.Null(entry.OneLiner);
        Assert.Equal(MemoryType.Observation, entry.Type);
        Assert.Equal(SyncAction.Unchanged, entry.Action);
    }

    // ─── MemoryLayer version fields ───────────────────────────────

    [Fact]
    public void MemoryLayer_VersionDefaults()
    {
        var layer = new MemoryLayer();
        Assert.Null(layer.Version);
        Assert.Null(layer.LastSyncedAt);
    }

    [Fact]
    public void MemoryLayer_VersionCanBeSet()
    {
        var layer = new MemoryLayer
        {
            Version = "1.2.3",
            LastSyncedAt = new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc),
        };
        Assert.Equal("1.2.3", layer.Version);
        Assert.NotNull(layer.LastSyncedAt);
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private static MemoryEntry MakeEntry(string content, MemoryType type, float importance, List<string> tags) =>
        new()
        {
            Id = $"memories/test/{type}/{Guid.NewGuid():N}"[..36],
            Content = content,
            Type = type,
            Importance = importance,
            Tags = tags,
            RepoId = "test-repo",
        };
}
