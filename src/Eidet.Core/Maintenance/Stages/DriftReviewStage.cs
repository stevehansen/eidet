using Eidet.Core.Configuration;
using Eidet.Core.Domain;

namespace Eidet.Core.Maintenance.Stages;

internal sealed class DriftReviewStage : IMaintenanceStage
{
    public const string StageName = "DriftReview";
    public string Name => StageName;

    private const int PageSize = 500;
    private const int SiblingLimit = 5;

    public async Task<StageOutcome> ExecuteAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        if (!ctx.Drift.Enabled || !ctx.Enrichment.IsAvailable) return new StageOutcome(Name, 0);

        var candidates = new List<MemoryEntry>();
        for (var skip = 0; ; skip += PageSize)
        {
            var page = await ctx.Store.BrowseAsync(ctx.RepoId, skip, PageSize, ct: ct);
            candidates.AddRange(page.Where(e =>
                e.IsLatest && e.LayerId == null && e.Validity.IsValidAt(ctx.Now)
                && (ctx.Now - e.CreatedAt).TotalDays >= ctx.Drift.MinAgeDays));
            if (page.Count < PageSize) break;
        }

        // Never-reviewed entries first, then the ones with the oldest verdicts — ReviewedAt
        // is the coverage cursor that walks the corpus across nightly runs. DistinctBy guards
        // against concurrent stores shifting BrowseAsync pages mid-collection.
        var batch = candidates
            .DistinctBy(e => e.Id)
            .OrderBy(e => e.Drift is null ? 0 : 1)
            .ThenBy(e => e.Drift?.ReviewedAt ?? DateTime.MinValue)
            .Take(ctx.Drift.NightlyBatch);

        var reviewed = 0;
        foreach (var entry in batch)
        {
            if (ct.IsCancellationRequested) break;

            var siblings = await FindNewerSiblingOneLinersAsync(ctx, entry, ct);
            var verdict = await ctx.Enrichment.ReviewDriftAsync(entry, siblings, ctx.Now, ct);
            if (verdict is null) continue; // no write — cursor untouched, retried a future night

            entry.Drift = verdict;
            if (verdict.Verdict != DriftVerdictKind.Ok && verdict.ModelConfidence >= ctx.Drift.MinModelConfidence)
            {
                switch (ctx.Drift.Autonomy)
                {
                    case DriftAutonomy.Decay:
                        entry.Confidence = DecayedConfidence(entry.Confidence);
                        break;
                    case DriftAutonomy.Expire:
                        entry.Confidence = DecayedConfidence(entry.Confidence);
                        // ForgetReason is stamped by TtlExpiryStage on actual expiry; the why lives in Drift.Reason.
                        if (entry.ForgetAfter is null)
                            entry.ForgetAfter = ctx.Now.AddDays(14);
                        break;
                }
            }

            await ctx.Write.WriteAsync(entry, ct);
            reviewed++;
        }

        return new StageOutcome(Name, reviewed);
    }

    private static async Task<IReadOnlyList<string>> FindNewerSiblingOneLinersAsync(
        MaintenanceContext ctx, MemoryEntry entry, CancellationToken ct)
    {
        var text = string.Join(' ', entry.Entities.Take(5));
        if (string.IsNullOrWhiteSpace(text))
            text = string.Join(' ', entry.Tags.Take(5));
        if (string.IsNullOrWhiteSpace(text)) return []; // nothing to search on — self-review only

        var hits = await ctx.Store.FullTextSearchAsync([ctx.RepoId], new MemoryQuery { Text = text, Limit = 8 }, ct);
        return hits
            .Where(h => h.Id != entry.Id && h.CreatedAt > entry.CreatedAt && h.IsLatest)
            .Take(SiblingLimit)
            .Select(h => h.OneLiner ?? h.Summary ?? Truncate(h.Content))
            .ToList();
    }

    private static string Truncate(string content) =>
        content.Length <= 120 ? content : content[..120];

    // Floor at 0.2 without ever raising confidence that's already below it (fizzle can push lower).
    private static float DecayedConfidence(float c) => Math.Max(Math.Min(c, 0.2f), c - 0.15f);
}
