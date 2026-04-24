using Eidet.Core.Domain;
using Eidet.Core.Gates;

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
}
