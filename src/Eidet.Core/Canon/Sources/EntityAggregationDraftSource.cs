using System.Runtime.CompilerServices;
using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Core.Canon.Sources;

/// <summary>
/// Term drafts from entity aggregation: it loads the repo's top-scored Insights, Procedures and
/// Heuristics (Observations excluded — session residue, per the membership filter), groups them by the
/// <see cref="MemoryEntry.Entities"/> they mention, and proposes a Term draft for each entity cited by at
/// least two distinct memories. The definition is the deterministic Portal Glossary fallback — the
/// one-liner (or truncated content) of the highest-importance citing memory — so this source needs no LLM.
/// </summary>
public sealed class EntityAggregationDraftSource : ICanonDraftSource
{
    // Bound the aggregation scan; GetTopScoredAsync already restricts to valid + latest memories.
    private const int MemberScanCap = 500;
    // A term worth a page is one several memories share — a single mention is not yet a glossary entry.
    private const int MinCitations = 2;
    private const int DefinitionMaxChars = 200;

    private readonly IEidetStore _store;

    public EntityAggregationDraftSource(IEidetStore store)
    {
        _store = store;
    }

    public string Name => "entity-aggregation";

    // Always available — it reads only the store, which every repo has.
    public bool AppliesTo(CanonProposalContext ctx) => true;

    public async IAsyncEnumerable<CanonDraftCandidate> ProposeAsync(
        CanonProposalContext ctx, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Canon pages are excluded from the member pool (the guard's third read path, beside the
        // consolidation and dedup candidate sets): an approved page mentioning its own term must not
        // re-enter as a citing member of that term's next draft — a self-referential DerivedFrom.
        var members = (await _store.GetTopScoredAsync(
                ctx.RepoId, [MemoryType.Insight, MemoryType.Procedure, MemoryType.Heuristic], MemberScanCap, ct))
            .Where(m => !CanonTags.IsCanonPage(m));

        var byEntity = new Dictionary<string, List<MemoryEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in members)
        foreach (var entity in m.Entities.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byEntity.TryGetValue(entity, out var citing))
                byEntity[entity] = citing = [];
            citing.Add(m);
        }

        foreach (var (entity, citing) in byEntity)
        {
            if (citing.Count < MinCitations) continue;

            var slug = CanonSlug.From(entity);
            if (string.IsNullOrEmpty(slug)) continue;

            var top = citing.OrderByDescending(m => m.Importance).First();
            var definition = !string.IsNullOrWhiteSpace(top.OneLiner)
                ? top.OneLiner!
                : Truncate(top.Content, DefinitionMaxChars);

            var memberIds = citing.Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var content = $"{entity}: {definition}";
            var fingerprint = CanonFingerprint.Of(CanonKind.Term, entity, content, memberIds);

            yield return new CanonDraftCandidate(CanonKind.Term, slug, entity, content, memberIds, fingerprint);
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
