using Eidet.Core.Domain;
using Eidet.Core.Enrichment;

namespace Eidet.Core.Maintenance.Stages;

/// <summary>
/// Retroactively cleans enrichment fields corrupted by LLM chain-of-thought leakage.
/// Applies <see cref="OllamaTextSanitizer.Clean"/> to Summary / OneLiner / ForesightHint;
/// nulls unsalvageable fields so they get re-enriched on the next Ollama pass.
/// </summary>
internal sealed class EnrichmentCleanupStage : IMaintenanceStage
{
    public const string StageName = "EnrichmentCleanup";
    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        var entries = await ctx.Store.GetTopScoredAsync(ctx.RepoId, Enum.GetValues<MemoryType>(), 500, ct);
        var cleaned = 0;

        foreach (var entry in entries)
        {
            var changed = false;

            var cleanedSummary = OllamaTextSanitizer.Clean(entry.Summary);
            if (cleanedSummary != entry.Summary)
            {
                entry.Summary = cleanedSummary;
                changed = true;
            }

            var cleanedOneLiner = OllamaTextSanitizer.Clean(entry.OneLiner);
            if (cleanedOneLiner != entry.OneLiner)
            {
                entry.OneLiner = cleanedOneLiner;
                changed = true;
            }

            var cleanedHint = OllamaTextSanitizer.Clean(entry.ForesightHint);
            if (cleanedHint != entry.ForesightHint)
            {
                entry.ForesightHint = cleanedHint;
                changed = true;
            }

            if (changed)
            {
                await ctx.Store.UpdateAsync(entry, ct);
                cleaned++;
            }
        }

        return new StageOutcome(Name, cleaned);
    }
}
