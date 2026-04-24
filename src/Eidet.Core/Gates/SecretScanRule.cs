using System.Text.RegularExpressions;
using Eidet.Core.Domain;

namespace Eidet.Core.Gates;

internal sealed partial class SecretScanRule : IValidationRule
{
    public const string GateName = "secret-scan";
    public string Name => GateName;

    private static readonly (Regex Pattern, string Description)[] Patterns =
    [
        (AwsKeyRegex(), "AWS access key"),
        (ApiSecretKeyRegex(), "API secret key"),
        (GitHubTokenRegex(), "GitHub token"),
        (BearerTokenRegex(), "Bearer token"),
        (JwtTokenRegex(), "JWT token"),
        (PrivateKeyRegex(), "private key"),
        (ConnectionPasswordRegex(), "connection string password"),
        (SecretEnvVarRegex(), "secret environment variable"),
        (Base64KeyRegex(), "base64-encoded key"),
        (NpmTokenRegex(), "npm token"),
        (AzureStorageKeyRegex(), "Azure storage key"),
        (GcpServiceAccountRegex(), "GCP service account key"),
        (SlackTokenRegex(), "Slack token"),
    ];

    public ValidationResult Check(string content, MemoryType type)
    {
        foreach (var (pattern, description) in Patterns)
        {
            if (pattern.IsMatch(content))
                return ValidationResult.Fail(GateName, $"Blocked: content contains {description}");
        }
        return ValidationResult.Pass();
    }

    [GeneratedRegex(@"AKIA[0-9A-Z]{16}")]
    private static partial Regex AwsKeyRegex();

    [GeneratedRegex(@"\b(sk-[a-zA-Z0-9]{20,}|sk_[a-zA-Z0-9]{20,})")]
    private static partial Regex ApiSecretKeyRegex();

    [GeneratedRegex(@"\b(ghp_[a-zA-Z0-9]{36}|gho_[a-zA-Z0-9]{36}|ghs_[a-zA-Z0-9]{36}|github_pat_[a-zA-Z0-9_]{82})")]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"\bBearer\s+[a-zA-Z0-9\-_\.]{40,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\beyJ[a-zA-Z0-9\-_]{20,}\.[a-zA-Z0-9\-_]{20,}\.")]
    private static partial Regex JwtTokenRegex();

    [GeneratedRegex(@"-----BEGIN\s+(RSA\s+)?PRIVATE\s+KEY-----")]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(Password|Pwd)\s*=\s*[^;\s]{3,}", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionPasswordRegex();

    [GeneratedRegex(@"\b(API_KEY|SECRET_KEY|ACCESS_TOKEN|AUTH_TOKEN|PRIVATE_KEY|CLIENT_SECRET)\s*[=:]\s*\S{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex SecretEnvVarRegex();

    [GeneratedRegex(@"\b[a-zA-Z0-9/+]{40,}==")]
    private static partial Regex Base64KeyRegex();

    [GeneratedRegex(@"\bnpm_[a-zA-Z0-9]{36}")]
    private static partial Regex NpmTokenRegex();

    [GeneratedRegex(@"(DefaultEndpointsProtocol|AccountKey)\s*=\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex AzureStorageKeyRegex();

    [GeneratedRegex(@"""private_key""\s*:\s*""-----BEGIN")]
    private static partial Regex GcpServiceAccountRegex();

    [GeneratedRegex(@"\b(xoxb-|xoxp-|xoxs-|xapp-)[a-zA-Z0-9\-]+")]
    private static partial Regex SlackTokenRegex();
}
