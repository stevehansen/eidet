using Eidet.Core.Gates;

namespace Eidet.Core.Tests.Gates;

public class SecretScannerTests
{
    [Fact]
    public void Scan_PassesCleanContent()
    {
        var result = SecretScanner.Scan("The API uses JWT authentication with role-based access control");
        Assert.True(result.Passed);
    }

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
    public void Scan_BlocksSecrets(string content, string expectedType)
    {
        var result = SecretScanner.Scan(content);
        Assert.False(result.Passed);
        Assert.Contains(expectedType, result.Reason);
    }

    [Fact]
    public void Scan_PassesShortSimilarStrings()
    {
        // "sk-" followed by less than 20 chars should pass
        var result = SecretScanner.Scan("Using sk-short key");
        Assert.True(result.Passed);
    }
}
