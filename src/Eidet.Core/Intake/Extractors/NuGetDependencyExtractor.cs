using System.Text.RegularExpressions;
using Eidet.Core.Domain;

namespace Eidet.Core.Intake.Extractors;

/// <summary>
/// Scans every <c>*.csproj</c> under the project root for <c>PackageReference</c>
/// (consumed dependencies → emitted as <c>nuget:</c>-prefixed <see cref="MemoryLink"/>)
/// and <c>PackageId</c> (the produced package, if any). Recursive walk so multi-project
/// solutions don't need any per-project config.
/// </summary>
public sealed partial class NuGetDependencyExtractor : IIntakeExtractor
{
    public string Name => "deps.nuget";

    public bool AppliesTo(IntakeContext ctx) =>
        Directory.Exists(ctx.ProjectPath) &&
        Directory.EnumerateFiles(ctx.ProjectPath, "*.csproj", SearchOption.AllDirectories).Any();

    public async Task ExtractAsync(IntakeContext ctx, IIntakeSink sink, CancellationToken ct)
    {
        foreach (var csproj in Directory.GetFiles(ctx.ProjectPath, "*.csproj", SearchOption.AllDirectories))
        {
            var content = await File.ReadAllTextAsync(csproj, ct);

            foreach (Match m in PackageReferenceRegex().Matches(content))
            {
                sink.AddLink(new MemoryLink
                {
                    TargetRepoId = $"nuget:{m.Groups[1].Value}",
                    Relation = "depends-on",
                });
            }

            var idMatch = PackageIdRegex().Match(content);
            if (idMatch.Success)
                sink.AddProducedPackage(idMatch.Groups[1].Value);
        }
    }

    [GeneratedRegex(@"<PackageReference\s+Include=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PackageReferenceRegex();

    [GeneratedRegex(@"<PackageId>([^<]+)</PackageId>")]
    private static partial Regex PackageIdRegex();
}
