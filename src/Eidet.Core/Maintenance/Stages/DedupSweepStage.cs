using Eidet.Core.Domain;
using Eidet.Core.Text;

namespace Eidet.Core.Maintenance.Stages;

internal sealed class DedupSweepStage : IMaintenanceStage
{
    public const string StageName = "DedupSweep";
    private const float SimilarityThreshold = 0.85f;

    public string Name => StageName;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        var merged = 0;

        foreach (var type in Enum.GetValues<MemoryType>())
        {
            var entries = await ctx.Store.GetTopScoredAsync(ctx.RepoId, [type], 200, ct);

            for (var i = 0; i < entries.Count; i++)
            {
                for (var j = i + 1; j < entries.Count; j++)
                {
                    var similarity = WordSimilarity.Compute(entries[i].Content, entries[j].Content);
                    if (similarity < SimilarityThreshold) continue;

                    var (keep, discard) = entries[i].Importance >= entries[j].Importance
                        ? (entries[i], entries[j])
                        : (entries[j], entries[i]);

                    keep.AccessCount += discard.AccessCount;
                    foreach (var tag in discard.Tags)
                    {
                        if (!keep.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                            keep.Tags.Add(tag);
                    }
                    await ctx.Store.UpdateAsync(keep, ct);

                    discard.Validity.ValidUntil = DateTime.UtcNow;
                    discard.ForgetReason = $"Dedup merged into {keep.Id}";
                    await ctx.Store.UpdateAsync(discard, ct);
                    merged++;
                }
            }
        }

        return new StageOutcome(Name, merged);
    }
}
