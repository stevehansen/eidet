using Eidet.Core.Domain;
using Eidet.Core.Enrichment;

namespace Eidet.Core.Tests.Services;

public class EnrichmentServiceDriftReviewTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private const string ValidOkJson =
        """{"verdict":"ok","confidence":0.9,"reason":"still sound","suggested_fix":null}""";

    private static MemoryEntry MakeEntry(
        string content = "The deployment pipeline runs migrations before starting the server.") => new()
    {
        Id = "memories/repo-a/insight/drift-1",
        RepoId = "repo-a",
        Type = MemoryType.Insight,
        Content = content,
        CreatedAt = Now.AddDays(-30),
    };

    private static EnrichmentService WithResponse(string? response) =>
        new(new InMemoryEnrichmentAdapter().SetResponse(EnrichmentPrompt.DriftReview, response),
            modelName: "test-model");

    // ─── Strict JSON happy path ───────────────────────────────────────────

    [Fact]
    public async Task ReviewDrift_StrictJson_MapsAllFields()
    {
        using var svc = WithResponse(
            """{"verdict":"stale","confidence":0.85,"reason":"references removed v1 API","suggested_fix":"rewrite against the v2 endpoint"}""");

        var review = await svc.ReviewDriftAsync(MakeEntry(), [], Now);

        Assert.NotNull(review);
        Assert.Equal(DriftVerdictKind.Stale, review.Verdict);
        Assert.Equal(0.85f, review.ModelConfidence);
        Assert.Equal("references removed v1 API", review.Reason);
        Assert.Equal("rewrite against the v2 endpoint", review.SuggestedFix);
        Assert.Equal(Now, review.ReviewedAt);
        Assert.Equal("test-model", review.Model);
    }

    [Theory]
    [InlineData("ok", DriftVerdictKind.Ok)]
    [InlineData("stale", DriftVerdictKind.Stale)]
    [InlineData("contradicted", DriftVerdictKind.Contradicted)]
    [InlineData("vague", DriftVerdictKind.Vague)]
    [InlineData("OK", DriftVerdictKind.Ok)] // verdict matching is case-insensitive
    public async Task ReviewDrift_MapsEveryVerdictKind(string verdict, DriftVerdictKind expected)
    {
        using var svc = WithResponse(
            $$"""{"verdict":"{{verdict}}","confidence":0.5,"reason":"r","suggested_fix":null}""");

        var review = await svc.ReviewDriftAsync(MakeEntry(), [], Now);

        Assert.NotNull(review);
        Assert.Equal(expected, review.Verdict);
    }

    // ─── Tolerated response wrappers ──────────────────────────────────────

    [Fact]
    public async Task ReviewDrift_JsonInCodeFences_Parses()
    {
        using var svc = WithResponse("```json\n" + ValidOkJson + "\n```");

        var review = await svc.ReviewDriftAsync(MakeEntry(), [], Now);

        Assert.NotNull(review);
        Assert.Equal(DriftVerdictKind.Ok, review.Verdict);
        Assert.Equal("still sound", review.Reason);
    }

    [Fact]
    public async Task ReviewDrift_ThinkTagPreamble_Parses()
    {
        using var svc = WithResponse(
            "<think>Let me check the siblings...\nNothing newer contradicts it.</think>" + ValidOkJson);

        var review = await svc.ReviewDriftAsync(MakeEntry(), [], Now);

        Assert.NotNull(review);
        Assert.Equal(DriftVerdictKind.Ok, review.Verdict);
    }

    [Fact]
    public async Task ReviewDrift_ProsePreambleAndTrailingText_ParsesFirstBalancedBlock()
    {
        using var svc = WithResponse(
            "The memory still matches what the newer siblings say.\n" + ValidOkJson + "\nHope this helps!");

        var review = await svc.ReviewDriftAsync(MakeEntry(), [], Now);

        Assert.NotNull(review);
        Assert.Equal(DriftVerdictKind.Ok, review.Verdict);
        Assert.Equal(0.9f, review.ModelConfidence);
    }

    [Theory]
    [InlineData("The config {x: 1} changed since this was written.\n")] // brace pair that is not JSON
    [InlineData("Compare {\"x\": 1} with the entry below.\n")] // valid JSON but not a verdict object
    public async Task ReviewDrift_BracePairInProseBeforeJson_ParsesLaterBlock(string preamble)
    {
        using var svc = WithResponse(preamble + ValidOkJson);

        var review = await svc.ReviewDriftAsync(MakeEntry(), [], Now);

        Assert.NotNull(review);
        Assert.Equal(DriftVerdictKind.Ok, review.Verdict);
    }

    [Fact]
    public async Task ReviewDrift_MultiLineReason_IsCollapsedToOneLine()
    {
        using var svc = WithResponse(
            """{"verdict":"stale","confidence":0.9,"reason":"references removed\n  v1 API","suggested_fix":null}""");

        var review = await svc.ReviewDriftAsync(MakeEntry(), [], Now);

        Assert.NotNull(review);
        Assert.Equal("references removed v1 API", review.Reason); // injected verbatim into recall warnings
    }

    // ─── Malformed responses map to null ──────────────────────────────────

    [Theory]
    [InlineData("complete garbage with no structure")]
    [InlineData("verdict: stale, confidence: 0.9")] // no JSON object
    [InlineData("{\"verdict\":\"stale\",\"confidence\":0.9")] // unbalanced braces
    [InlineData("{\"verdict\":\"fresh\",\"confidence\":0.9,\"reason\":\"r\",\"suggested_fix\":null}")] // unknown verdict
    [InlineData("{\"verdict\": [1,2,}")] // balanced but invalid JSON
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ReviewDrift_MalformedResponse_ReturnsNull(string? response)
    {
        using var svc = WithResponse(response);

        Assert.Null(await svc.ReviewDriftAsync(MakeEntry(), [], Now));
    }

    // ─── Confidence clamping ──────────────────────────────────────────────

    [Theory]
    [InlineData("1.7", 1.0f)]
    [InlineData("-0.3", 0.0f)]
    public async Task ReviewDrift_ClampsConfidenceIntoUnitRange(string confidence, float expected)
    {
        using var svc = WithResponse(
            $$"""{"verdict":"stale","confidence":{{confidence}},"reason":"r","suggested_fix":null}""");

        var review = await svc.ReviewDriftAsync(MakeEntry(), [], Now);

        Assert.NotNull(review);
        Assert.Equal(expected, review.ModelConfidence);
    }

    // ─── Short-circuits ───────────────────────────────────────────────────

    [Fact]
    public async Task ReviewDrift_UnavailablePort_ReturnsNull()
    {
        var adapter = new InMemoryEnrichmentAdapter { IsAvailable = false }
            .SetResponse(EnrichmentPrompt.DriftReview, ValidOkJson);
        using var svc = new EnrichmentService(adapter, modelName: "test-model");

        Assert.Null(await svc.ReviewDriftAsync(MakeEntry(), [], Now));
    }

    [Fact]
    public async Task ReviewDrift_BlankContent_ReturnsNullWithoutCallingPort()
    {
        var called = false;
        var adapter = new InMemoryEnrichmentAdapter()
            .SetResponder(EnrichmentPrompt.DriftReview, _ => { called = true; return ValidOkJson; });
        using var svc = new EnrichmentService(adapter, modelName: "test-model");

        Assert.Null(await svc.ReviewDriftAsync(MakeEntry(content: "   "), [], Now));
        Assert.False(called);
    }
}
