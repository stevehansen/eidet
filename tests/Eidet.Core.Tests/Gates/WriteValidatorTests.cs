using Eidet.Core.Domain;
using Eidet.Core.Gates;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Gates;

public class WriteValidatorTests
{
    // ─── Happy path ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_PassesValidContent()
    {
        var result = WriteValidator.Validate("The RavenDB index uses Corax engine for full-text search");
        Assert.True(result.Passed);
        Assert.Null(result.FailedGate);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Validate_PassesCleanContentWithoutSecrets()
    {
        var result = WriteValidator.Validate("The API uses JWT authentication with role-based access control");
        Assert.True(result.Passed);
    }

    // ─── Secret scanning ──────────────────────────────────────────────────

    [Theory]
    [InlineData("My key is AKIAIOSFODNN7EXAMPLE", "AWS access key")]
    [InlineData("Using sk-abcdefghijklmnopqrstuvwxyz for auth", "API secret key")]
    [InlineData("Using sk_abcdefghijklmnopqrstuvwxyz for auth", "API secret key")]
    [InlineData("Token: ghp_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "GitHub token")]
    [InlineData("Token eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwiYWRtaW4iOnRydWV9.", "JWT token")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nMIIEv...", "private key")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEv...", "private key")]
    [InlineData("Server=localhost;Password=secret123", "connection string password")]
    [InlineData("API_KEY=sk_live_abcdef12345678", "secret environment variable")]
    [InlineData("CLIENT_SECRET: verylongsecretvalue123", "secret environment variable")]
    [InlineData("npm_abcdefghijklmnopqrstuvwxyz0123456789", "npm token")]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=test;AccountKey=abc123", "Azure storage key")]
    [InlineData("{\"private_key\": \"-----BEGIN RSA PRIVATE KEY", "GCP service account key")]
    [InlineData("Bot token is xoxb-123456-abcdef", "Slack token")]
    [InlineData("User token xoxp-999-abc-def", "Slack token")]
    public void Validate_BlocksSecrets(string content, string expectedType)
    {
        var result = WriteValidator.Validate(content);
        Assert.False(result.Passed);
        Assert.Contains(expectedType, result.Reason);
        Assert.Equal("secret-scan", result.FailedGate);
    }

    [Fact]
    public void Validate_PassesShortSimilarStringsToSecrets()
    {
        // "sk-" followed by fewer than 20 chars should not match as a secret
        var result = WriteValidator.Validate("Using sk-short key for the legacy test harness");
        Assert.True(result.Passed);
    }

    // ─── Signal gate ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlocksEmptyContent(string content)
    {
        var result = WriteValidator.Validate(content);
        Assert.False(result.Passed);
        Assert.Equal("signal", result.FailedGate);
    }

    [Fact]
    public void Validate_BlocksShortContent()
    {
        var result = WriteValidator.Validate("too short");
        Assert.False(result.Passed);
        Assert.Contains("too short", result.Reason);
        Assert.Equal("signal", result.FailedGate);
    }

    [Theory]
    [InlineData("tests passed")]
    [InlineData("it works")]
    [InlineData("done")]
    [InlineData("no changes")]
    [InlineData("build succeeded")]
    [InlineData("modified")]
    public void Validate_BlocksLowSignalPhrases(string content)
    {
        var result = WriteValidator.Validate(content);
        Assert.False(result.Passed);
        Assert.Equal("signal", result.FailedGate);
    }

    [Theory]
    [InlineData("tests passed.")]
    [InlineData("no changes.")]
    public void Validate_BlocksLowSignalPhrasesWithTrailingPeriod(string content)
    {
        var result = WriteValidator.Validate(content);
        Assert.False(result.Passed);
        Assert.Equal("signal", result.FailedGate);
    }

    [Theory]
    [InlineData("I will check the database connection next")]
    [InlineData("Let me look at the configuration file")]
    [InlineData("I'm going to run the tests now")]
    public void Validate_BlocksAgentSelfTalkForObservations(string content)
    {
        var result = WriteValidator.Validate(content, MemoryType.Observation);
        Assert.False(result.Passed);
        Assert.Contains("self-talk", result.Reason);
        Assert.Equal("signal", result.FailedGate);
    }

    [Fact]
    public void Validate_AllowsSelfTalkPhraseForNonObservations()
    {
        var result = WriteValidator.Validate(
            "I will always run migrations before tests in this repo",
            MemoryType.Heuristic);
        Assert.True(result.Passed);
    }

    // ─── Composition / short-circuit semantics ────────────────────────────

    [Fact]
    public void Validate_SecretScanRunsBeforeSignalCheck()
    {
        // Content contains a secret AND is short-ish — secret must fire first.
        var result = WriteValidator.Validate("AKIAIOSFODNN7EXAMPLE");
        Assert.False(result.Passed);
        Assert.Equal("secret-scan", result.FailedGate);
    }

    [Fact]
    public void Validate_TypeAwareGateApplied()
    {
        // Self-talk should be blocked for Observation.
        var obsResult = WriteValidator.Validate(
            "I will check the database connection next", MemoryType.Observation);
        Assert.False(obsResult.Passed);
        Assert.Equal("signal", obsResult.FailedGate);

        // Same content passes for Heuristic.
        var heurResult = WriteValidator.Validate(
            "I will always run migrations before tests in this repo", MemoryType.Heuristic);
        Assert.True(heurResult.Passed);
    }

    // ─── BuildEntry: happy path ───────────────────────────────────────────

    [Fact]
    public void BuildEntry_BuildsEntryFromValidOptions()
    {
        var opts = new StoreOptions(
            RepoId: @"C:\Projects\MyApp",
            Content: "MemoryService.StoreAsync persists each memory as a RavenDB document",
            Type: MemoryType.Insight)
        {
            Tags = ["storage", "ravendb"],
            Importance = 0.8f,
            Source = "user",
            SessionId = "sess-42",
        };

        var built = WriteValidator.BuildEntry(opts);

        Assert.True(built.IsBuilt);
        Assert.Null(built.RejectionReason);

        var entry = built.Entry!;
        Assert.Equal("C--Projects-MyApp", entry.RepoId);
        Assert.Equal("MemoryService.StoreAsync persists each memory as a RavenDB document", entry.Content);
        Assert.Equal(MemoryType.Insight, entry.Type);
        Assert.Equal(["storage", "ravendb"], entry.Tags);
        Assert.Equal(0.8f, entry.Importance);
        Assert.Equal("user", entry.Source);
        Assert.Equal("sess-42", entry.SourceSessionId);
        Assert.True(entry.IsLatest);
        Assert.NotEmpty(entry.Entities);
        Assert.False(string.IsNullOrWhiteSpace(entry.OneLiner));
    }

    [Fact]
    public void BuildEntry_NormalizesRepoIdAndGeneratesScopedId()
    {
        var opts = new StoreOptions(
            RepoId: @"C:\Projects\MyApp",
            Content: "The Corax full-text engine backs hybrid search in this repo",
            Type: MemoryType.Observation);

        var entry = WriteValidator.BuildEntry(opts).Entry!;

        Assert.Equal("C--Projects-MyApp", entry.RepoId);
        // Id is scoped to the normalized repo + type: memories/{repo}/{type}/{hash}
        Assert.StartsWith("memories/C--Projects-MyApp/observation/", entry.Id);
    }

    [Fact]
    public void BuildEntry_MapsSupersedesToParentMemoryId()
    {
        var opts = new StoreOptions(
            RepoId: "repo-a",
            Content: "The replacement insight supersedes the prior version of this fact",
            Type: MemoryType.Insight)
        {
            Supersedes = "memories/repo-a/insight/oldhash00000",
        };

        var entry = WriteValidator.BuildEntry(opts).Entry!;

        Assert.Equal("memories/repo-a/insight/oldhash00000", entry.ParentMemoryId);
    }

    [Theory]
    [InlineData(5.0f, 1.0f)]
    [InlineData(-2.0f, 0.0f)]
    public void BuildEntry_ClampsImportanceToUnitInterval(float input, float expected)
    {
        var opts = new StoreOptions(
            RepoId: "repo-a",
            Content: "The scheduler uses the RavenDB Refresh feature as a persisted alarm clock",
            Type: MemoryType.Observation)
        {
            Importance = input,
        };

        var entry = WriteValidator.BuildEntry(opts).Entry!;

        Assert.Equal(expected, entry.Importance);
    }

    [Fact]
    public void BuildEntry_ResolvesProvenanceFromSourceWhenNotSupplied()
    {
        // "user" source resolves to UserStated (confidence 0.7); default "claude-session" → AgentInferred (0.6).
        var userOpts = new StoreOptions(
            RepoId: "repo-a",
            Content: "The write path runs the secret scanner before any storage occurs",
            Type: MemoryType.Observation)
        {
            Source = "user",
        };
        var userEntry = WriteValidator.BuildEntry(userOpts).Entry!;
        Assert.Equal(MemoryProvenance.UserStated, userEntry.Provenance);

        var agentOpts = userOpts with { Source = "claude-session" };
        var agentEntry = WriteValidator.BuildEntry(agentOpts).Entry!;
        Assert.Equal(MemoryProvenance.AgentInferred, agentEntry.Provenance);
    }

    [Fact]
    public void BuildEntry_UsesExplicitProvenanceOverSource()
    {
        var opts = new StoreOptions(
            RepoId: "repo-a",
            Content: "This insight was produced by the consolidation maintenance stage",
            Type: MemoryType.Insight)
        {
            Source = "claude-session",
            Provenance = MemoryProvenance.Consolidation,
        };

        var entry = WriteValidator.BuildEntry(opts).Entry!;

        Assert.Equal(MemoryProvenance.Consolidation, entry.Provenance);
    }

    [Fact]
    public void BuildEntry_DefaultsTagsToEmptyListWhenNull()
    {
        var opts = new StoreOptions(
            RepoId: "repo-a",
            Content: "The embedded Web UI ships as a vanilla HTML/CSS/JS single-page app",
            Type: MemoryType.Observation);

        var entry = WriteValidator.BuildEntry(opts).Entry!;

        Assert.NotNull(entry.Tags);
        Assert.Empty(entry.Tags);
    }

    // ─── BuildEntry: rejection ────────────────────────────────────────────

    [Fact]
    public void BuildEntry_RejectsSecretContent()
    {
        var opts = new StoreOptions(
            RepoId: "repo-a",
            Content: "The deploy key is AKIAIOSFODNN7EXAMPLE for the staging bucket",
            Type: MemoryType.Observation);

        var built = WriteValidator.BuildEntry(opts);

        Assert.False(built.IsBuilt);
        Assert.Null(built.Entry);
        Assert.Contains("AWS access key", built.RejectionReason);
    }

    [Fact]
    public void BuildEntry_RejectsLowSignalContent()
    {
        var opts = new StoreOptions(
            RepoId: "repo-a",
            Content: "done",
            Type: MemoryType.Observation);

        var built = WriteValidator.BuildEntry(opts);

        Assert.False(built.IsBuilt);
        Assert.Null(built.Entry);
        Assert.NotNull(built.RejectionReason);
    }

    // ─── BuildEditEntry: carry-forward ────────────────────────────────────

    [Fact]
    public void BuildEditEntry_CarriesForwardCountersLinksAndDerivedFrom()
    {
        var original = new MemoryEntry
        {
            Id = "memories/repo-a/insight/abc123",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "The old content that is about to be superseded by an edit",
            EchoCount = 7,
            FizzleCount = 3,
            AccessCount = 11,
            Links = [new MemoryLink { TargetRepoId = "repo-b", TargetMemoryId = "memories/repo-b/insight/xyz", Relation = "refines" }],
            DerivedFrom = ["memories/repo-a/observation/seed1", "memories/repo-a/observation/seed2"],
        };

        var built = WriteValidator.BuildEditEntry(original,
            new EditOptions { Content = "The rewritten content that reflects the current architecture" });

        Assert.True(built.IsBuilt);
        var entry = built.Entry!;
        Assert.Equal(7, entry.EchoCount);
        Assert.Equal(3, entry.FizzleCount);
        Assert.Equal(11, entry.AccessCount);
        Assert.Equal(original.Links, entry.Links);
        Assert.Equal(original.DerivedFrom, entry.DerivedFrom);
        Assert.Equal(original.Id, entry.ParentMemoryId);
        Assert.Null(entry.Drift);
    }

    // ─── Drift cleared on edit ────────────────────────────────────────────

    [Fact]
    public void BuildEditEntry_ClearsDriftVerdictOnSupersedingVersion()
    {
        var original = new MemoryEntry
        {
            Id = "memories/repo-a/insight/abc123",
            RepoId = "repo-a",
            Type = MemoryType.Insight,
            Content = "The old content that drifted out of date",
            Drift = new DriftReview
            {
                Verdict = DriftVerdictKind.Stale,
                ModelConfidence = 0.9f,
                Reason = "outdated",
                ReviewedAt = DateTime.UtcNow,
            },
        };

        var built = WriteValidator.BuildEditEntry(original,
            new EditOptions { Content = "The rewritten content that reflects the current architecture" });

        Assert.True(built.IsBuilt);
        Assert.Null(built.Entry!.Drift); // the superseding version starts un-reviewed
    }

    // ─── Decomposed secret scan (memory-tool write path) ──────────────────

    [Fact]
    public void ScanSecrets_FlagsSecretWithoutSignalGates()
    {
        // Short content: fails the signal gate in Validate but ScanSecrets alone passes —
        // the memory-tool blob path runs ONLY the secret scan.
        Assert.False(WriteValidator.Validate("ok").Passed);
        Assert.True(WriteValidator.ScanSecrets("ok").Passed);

        var hit = WriteValidator.ScanSecrets("key AKIAIOSFODNN7EXAMPLE here");
        Assert.False(hit.Passed);
        Assert.Equal("secret-scan", hit.FailedGate);
        Assert.Contains("AWS access key", hit.Reason);
    }

    [Fact]
    public void RedactSecrets_ReplacesMatchesWithStableMarker()
    {
        var redacted = WriteValidator.RedactSecrets("key AKIAIOSFODNN7EXAMPLE here", out var count);

        Assert.Equal(1, count);
        Assert.Equal("key [REDACTED:AWS access key] here", redacted);
        Assert.True(WriteValidator.ScanSecrets(redacted).Passed); // marker itself is clean
    }

    [Fact]
    public void RedactSecrets_CleanContentUntouched()
    {
        var content = "The auth module uses JWT with role-based access control";
        var redacted = WriteValidator.RedactSecrets(content, out var count);

        Assert.Equal(0, count);
        Assert.Equal(content, redacted);
    }
}
