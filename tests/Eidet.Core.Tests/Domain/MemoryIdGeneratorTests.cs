using Eidet.Core.Domain;

namespace Eidet.Core.Tests.Domain;

public class MemoryIdGeneratorTests
{
    [Fact]
    public void Generate_ProducesCorrectFormat()
    {
        var id = MemoryIdGenerator.Generate("P--Eidet", MemoryType.Observation, "test content", DateTime.UtcNow);

        Assert.StartsWith("memories/P--Eidet/observation/", id);
        Assert.Equal(12, id.Split('/').Last().Length);
    }

    [Fact]
    public void Generate_DeterministicForSameInput()
    {
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var id1 = MemoryIdGenerator.Generate("repo", MemoryType.Insight, "content", now);
        var id2 = MemoryIdGenerator.Generate("repo", MemoryType.Insight, "content", now);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void Generate_DifferentForDifferentContent()
    {
        var now = DateTime.UtcNow;
        var id1 = MemoryIdGenerator.Generate("repo", MemoryType.Insight, "content A", now);
        var id2 = MemoryIdGenerator.Generate("repo", MemoryType.Insight, "content B", now);

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void Generate_IncludesTypeInPath()
    {
        var now = DateTime.UtcNow;

        Assert.Contains("/observation/", MemoryIdGenerator.Generate("r", MemoryType.Observation, "c", now));
        Assert.Contains("/insight/", MemoryIdGenerator.Generate("r", MemoryType.Insight, "c", now));
        Assert.Contains("/procedure/", MemoryIdGenerator.Generate("r", MemoryType.Procedure, "c", now));
        Assert.Contains("/heuristic/", MemoryIdGenerator.Generate("r", MemoryType.Heuristic, "c", now));
    }

    // ─── The hash preimage is a frozen persisted format (#80) ─────────────

    /// <summary>
    /// GOLDEN VALUE — do not "update the expectation" when this fails.
    ///
    /// Since #80 the id IS the memory's content commitment: <see cref="Eidet.Core.Memory.MemoryCommitment"/>
    /// re-derives it on the read path to detect content rewritten under a live id. That makes the preimage
    /// (repoId, type, content, createdAt — their order and their rendering) a persisted wire contract, not
    /// an implementation detail. Every id ever minted is already stored, so changing how Generate builds
    /// its input invalidates all of them at once: nothing throws, nothing fails to build, but every live
    /// memory silently reads as Broken and gets de-boosted to 0.25 at recall while the quality dashboard
    /// fills with Critical commitment-broken findings.
    ///
    /// This literal is the guard that turns that silent corpus-wide de-boost into a loud CI failure.
    /// If it breaks, the change to Generate is the bug.
    /// </summary>
    [Fact]
    public void Generate_GoldenValue_FreezesTheHashPreimage()
    {
        var id = MemoryIdGenerator.Generate(
            "P--Eidet", MemoryType.Insight,
            "the memory id preimage is a frozen persisted format",
            new DateTime(2026, 1, 15, 12, 30, 45, DateTimeKind.Utc));

        Assert.Equal("memories/P--Eidet/insight/760bf49b9d25", id);
    }

    /// <summary>
    /// Generate normalizes <see cref="DateTime.Kind"/> before rendering "O", so the preimage is
    /// Kind-independent. It has to be: "O" appends "Z" for Utc and nothing for Unspecified, so a
    /// serializer round trip that dropped Kind would otherwise change the hash and report the whole
    /// corpus as tampered.
    /// </summary>
    [Fact]
    public void Generate_NormalizesKind_SameInstantYieldsSameId()
    {
        var utc = new DateTime(2026, 1, 15, 12, 30, 45, DateTimeKind.Utc);
        var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
        // Round-tripped through the local zone so the assertion holds on a CI box in any timezone:
        // the Local value denotes the same instant, whatever the offset happens to be.
        var local = utc.ToLocalTime();
        Assert.Equal(DateTimeKind.Local, local.Kind);

        var fromUtc = MemoryIdGenerator.Generate("repo", MemoryType.Insight, "content", utc);
        var fromUnspecified = MemoryIdGenerator.Generate("repo", MemoryType.Insight, "content", unspecified);
        var fromLocal = MemoryIdGenerator.Generate("repo", MemoryType.Insight, "content", local);

        Assert.Equal(fromUtc, fromUnspecified);
        Assert.Equal(fromUtc, fromLocal);
    }
}
