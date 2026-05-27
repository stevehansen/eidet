using Eidet.Core.Domain;
using Eidet.Core.Enrichment;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Eidet.Core.Text;

namespace Eidet.Core.Maintenance;

/// <summary>
/// Dedup engine: folds near-duplicate memories of the same type into a single survivor.
/// Runs a semantic pass (vector recall, catches paraphrases the lexical scan misses) followed
/// by the original lexical Jaccard pass (so embeddings-less installs don't regress), both feeding
/// one deterministic merge routine. Exposed publicly so API / MCP / scheduler can run dedup in
/// dry-run or stand-alone mode without spinning up the full maintenance pipeline.
/// </summary>
public sealed class DedupEngine
{
    private readonly IEidetStore _store;
    private readonly EnrichmentService _enrichment;
    private readonly MemoryService? _memory;

    public DedupEngine(IEidetStore store, EnrichmentService? enrichment = null, MemoryService? memory = null)
    {
        _store = store;
        _enrichment = enrichment ?? EnrichmentService.CreateNull();
        _memory = memory;
    }

    public Task<DedupResult> DedupAsync(string repoId, bool dryRun = false, CancellationToken ct = default) =>
        DedupAsync(repoId, new DedupOptions(), dryRun, ct);

    public async Task<DedupResult> DedupAsync(string repoId, DedupOptions options, bool dryRun = false, CancellationToken ct = default)
    {
        var result = new DedupResult();
        var types = options.Types ?? Enum.GetValues<MemoryType>();

        foreach (var type in types)
        {
            var entries = await _store.GetTopScoredAsync(repoId, [type], options.CandidatesPerType, ct);
            var byId = entries.ToDictionary(e => e.Id);
            var claimed = new HashSet<string>();

            // Semantic pass: catches paraphrased near-duplicates the lexical scan can't see.
            foreach (var entry in entries)
            {
                if (claimed.Contains(entry.Id)) continue;

                var cands = await _store.FindNearDuplicatesAsync(repoId, entry, options.SemanticThreshold, 10, ct);
                foreach (var cand in cands)
                {
                    if (claimed.Contains(entry.Id)) break;   // entry itself was folded away — stop merging into a tombstone
                    if (cand.Id == entry.Id || claimed.Contains(cand.Id)) continue;
                    if (!byId.TryGetValue(cand.Id, out var local)) continue;
                    await MergeAsync(entry, local, claimed, result, dryRun, ct);
                }
            }

            // Lexical pass: original O(n²) Jaccard scan, preserved for embeddings-less installs.
            for (var i = 0; i < entries.Count; i++)
            {
                if (claimed.Contains(entries[i].Id)) continue;
                for (var j = i + 1; j < entries.Count; j++)
                {
                    if (claimed.Contains(entries[j].Id)) continue;
                    if (WordSimilarity.Compute(entries[i].Content, entries[j].Content) < LexicalThreshold) continue;
                    await MergeAsync(entries[i], entries[j], claimed, result, dryRun, ct);
                }
            }
        }

        if (!dryRun && result.Merges.Count > 0)
            _memory?.InvalidateRecallCache(repoId);

        return result;
    }

    private async Task MergeAsync(
        MemoryEntry a, MemoryEntry b, HashSet<string> claimed, DedupResult result, bool dryRun, CancellationToken ct)
    {
        var (keep, discard) = a.Importance >= b.Importance ? (a, b) : (b, a);

        keep.AccessCount += discard.AccessCount;
        foreach (var tag in discard.Tags)
        {
            if (!keep.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                keep.Tags.Add(tag);
        }

        discard.Validity.ValidUntil = DateTime.UtcNow;
        discard.ForgetReason = $"Dedup merged into {keep.Id}";

        claimed.Add(discard.Id);
        result.Merges.Add(new DedupPair(keep.Id, discard.Id));

        if (!dryRun)
        {
            await _store.UpdateAsync(keep, ct);
            await _store.UpdateAsync(discard, ct);
        }
    }

    private const float LexicalThreshold = 0.85f;
}

public sealed record DedupOptions
{
    public float SemanticThreshold { get; init; } = 0.86f;   // below the 0.92 write-gate; catches the paraphrase gap
    public IReadOnlyList<MemoryType>? Types { get; init; }     // null => all four
    public int CandidatesPerType { get; init; } = 200;
}

public sealed class DedupResult
{
    public List<DedupPair> Merges { get; init; } = [];
    public int MergedCount => Merges.Count;
}

public readonly record struct DedupPair(string KeptId, string DiscardedId);
