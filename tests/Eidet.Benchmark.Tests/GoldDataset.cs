using Eidet.Core.Benchmark;
using Eidet.Core.Domain;
using Eidet.Core.Storage;

namespace Eidet.Benchmark.Tests;

/// <summary>
/// Curated, adversarial in-repo gold dataset for the <see cref="AmaCapability.Recall"/> heading.
///
/// The win cases model the failure mode issue #33 fixed: the gold memory has <b>combined</b> relevance
/// — present in both arms, strong in one — so its min-max-normalized fused score strictly exceeds any
/// single-arm distractor (no ties, robust to sort order), while the flat baseline, which discards the
/// arm scores and keeps lexical hits in backend order ahead of the vector-only tail, truncates the
/// vector-strong-but-lexically-late gold at the budget boundary. On several cases the baseline still
/// keeps gold (it lands inside k) but ranks it far lower, so the lift there is on MRR/nDCG, not recall.
///
/// The set deliberately includes cases the v2 ranker does <b>not</b> ace — gold genuinely weak in both
/// arms (case 10), a type budget that caps survival for every ranker (case 11), and a distractor that
/// is simply more relevant than gold (case 12) — plus a both-arms-strong sanity anchor (case 9) the
/// baseline also wins. So the scorecard reports a real, non-perfect number, not a self-drawn curve.
///
/// Cases 13-14 exercise graph-neighbor expansion (#33 item 7): the gold is in <b>neither</b> arm, reachable
/// only as a link-neighbor of a top-ranked parent. Fusion-with-expansion rescues it via damped inheritance;
/// the baseline never expands and scores recall 0 — an honest lift attributable purely to the graph signal.
///
/// All entries share a fixed <see cref="Now"/> creation time, so recency is a constant 1.0 and the
/// fused ranking is driven purely by the normalized lexical+vector blend (plus the damped link
/// inheritance on cases 13-14). Memory types are varied so the type-budget pass genuinely engages.
/// </summary>
public static class GoldDataset
{
    /// <summary>Fixed clock — every entry is "just created" so recency is identical and constant.</summary>
    public static readonly DateTime Now = new(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);

    private static MemoryEntry Entry(string id, MemoryType type) => new()
    {
        Id = id,
        RepoId = "bench-repo",
        Type = type,
        Content = id,
        CreatedAt = Now,
        LastAccessedAt = null,
        IsLatest = true,
        Validity = new Validity { ValidFrom = Now },
        Importance = 0.5f,
    };

    private static ScoredHit Hit(string id, MemoryType type, double score) => new(Entry(id, type), score);

    /// <summary>A scored hit whose entry carries an outbound link to <paramref name="linkTo"/> — the
    /// parent that lets graph expansion reach an off-pool gold.</summary>
    private static ScoredHit HitLinkedTo(string id, MemoryType type, double score, string linkTo)
    {
        var entry = Entry(id, type);
        entry.Links.Add(new MemoryLink { TargetRepoId = "bench-repo", TargetMemoryId = linkTo, Relation = "refines" });
        return new ScoredHit(entry, score);
    }

    /// <summary>Off-pool neighbors keyed by id, for a case's <see cref="BenchmarkCase.Neighbors"/> map.</summary>
    private static IReadOnlyDictionary<string, MemoryEntry> Neighbors(params (string Id, MemoryType Type)[] entries) =>
        entries.ToDictionary(e => e.Id, e => Entry(e.Id, e.Type), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> Gold(params string[] ids) =>
        ids.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<BenchmarkCase> Cases => _cases;

    private static readonly IReadOnlyList<BenchmarkCase> _cases =
    [
        // ── 1. Vector-strong gold buried late in a lexical flood (k=2) ──
        // Gold is lexically near the bottom (normLex 0.25) but tops the vector arm. Fusion lifts it to
        // #1; the flat baseline keeps gold in the 1.0 lexical tier but behind two higher-relevance
        // lexical distractors, so a k=2 budget drops it.
        new("recall-vec-strong-gold-buried-in-lex-flood", AmaCapability.Recall,
            Lex:
            [
                Hit("d1", MemoryType.Insight, 10.0),
                Hit("d2", MemoryType.Insight, 8.0),
                Hit("gold", MemoryType.Insight, 4.0),
                Hit("d3", MemoryType.Observation, 2.0),
            ],
            Vec:
            [
                Hit("gold", MemoryType.Insight, 10.0),
                Hit("v1", MemoryType.Insight, 2.0),
            ],
            GoldIds: Gold("gold"), K: 2),

        // ── 2. Combined gold: mid lexical, top vector (k=2) ──
        // Gold is 2nd in the lexical arm, so the baseline still keeps it at k=2 — but ranks it #2 while
        // fusion ranks it #1. Lift here is on MRR/nDCG, not recall: fusion improves ordering even when
        // the baseline doesn't lose the gold outright.
        new("recall-combined-gold-mid-lex-top-vec", AmaCapability.Recall,
            Lex:
            [
                Hit("lexOnly1", MemoryType.Insight, 10.0),
                Hit("gold", MemoryType.Procedure, 7.5),
                Hit("lexOnly2", MemoryType.Observation, 5.0),
            ],
            Vec:
            [
                Hit("gold", MemoryType.Procedure, 10.0),
                Hit("vecOnly1", MemoryType.Insight, 5.0),
                Hit("vecOnly2", MemoryType.Insight, 1.0),
            ],
            GoldIds: Gold("gold"), K: 2),

        // ── 3. Heuristic gold, tiny lexical signal, top vector, lexically last (k=2) ──
        // A different type (Heuristic) and a deep lexical arm: gold has the smallest lexical score but
        // tops vector. Fusion #1; baseline truncates it (4th in the lexical tier).
        new("recall-heuristic-gold-vec-led", AmaCapability.Recall,
            Lex:
            [
                Hit("a1", MemoryType.Observation, 10.0),
                Hit("a2", MemoryType.Observation, 8.0),
                Hit("a3", MemoryType.Insight, 6.0),
                Hit("gold", MemoryType.Heuristic, 3.0),
                Hit("a4", MemoryType.Insight, 2.0),
            ],
            Vec:
            [
                Hit("gold", MemoryType.Heuristic, 10.0),
                Hit("b1", MemoryType.Insight, 3.0),
            ],
            GoldIds: Gold("gold"), K: 2),

        // ── 4. Two combined golds, both vector-led, behind a lexical noise wall (k=3) ──
        // Fusion ranks both golds top-2; the baseline keeps only one (the other is 4th in the lexical
        // tier) → recall and survival lift of 0.5.
        new("recall-two-combined-golds", AmaCapability.Recall,
            Lex:
            [
                Hit("lexNoise1", MemoryType.Insight, 10.0),
                Hit("lexNoise2", MemoryType.Insight, 9.0),
                Hit("goldA", MemoryType.Insight, 6.0),
                Hit("goldB", MemoryType.Procedure, 4.0),
                Hit("lexlow", MemoryType.Observation, 2.0),
            ],
            Vec:
            [
                Hit("goldA", MemoryType.Insight, 9.0),
                Hit("goldB", MemoryType.Procedure, 10.0),
                Hit("vecNoise", MemoryType.Observation, 7.0),
            ],
            GoldIds: Gold("goldA", "goldB"), K: 3),

        // ── 5. Type-budget heuristic gold, generous k=4 ──
        // k=4 is generous enough that the baseline keeps gold (3rd in lexical), but ranks it low; fusion
        // ranks it #1. MRR/nDCG lift.
        new("recall-type-budget-heuristic-gold", AmaCapability.Recall,
            Lex:
            [
                Hit("i1", MemoryType.Insight, 10.0),
                Hit("i2", MemoryType.Insight, 9.0),
                Hit("gold", MemoryType.Heuristic, 6.0),
                Hit("i5", MemoryType.Insight, 3.0),
            ],
            Vec:
            [
                Hit("gold", MemoryType.Heuristic, 10.0),
                Hit("i3", MemoryType.Insight, 4.0),
            ],
            GoldIds: Gold("gold"), K: 4),

        // ── 6. Narrow vector margin: lexically tied, separated only by the vector arm (k=1) ──
        // Gold and the distractor tie lexically (both normalize to 1.0); only gold appears in vector.
        // Fusion's combined score (2.0) strictly beats the distractor (1.0); the baseline, blind to the
        // arm scores, keeps the distractor first by merge order and truncates gold at k=1.
        new("recall-narrow-vec-margin", AmaCapability.Recall,
            Lex:
            [
                Hit("distractor", MemoryType.Insight, 7.0),
                Hit("gold", MemoryType.Insight, 7.0),
            ],
            Vec:
            [
                Hit("gold", MemoryType.Insight, 9.0),
            ],
            GoldIds: Gold("gold"), K: 1),

        // ── 7. Deep pool, single vector-led combined gold (k=2) ──
        new("recall-deep-pool-single-gold", AmaCapability.Recall,
            Lex:
            [
                Hit("L1", MemoryType.Insight, 10.0),
                Hit("L2", MemoryType.Observation, 9.0),
                Hit("gold", MemoryType.Procedure, 7.0),
                Hit("L3", MemoryType.Insight, 6.0),
                Hit("L4", MemoryType.Observation, 5.0),
            ],
            Vec:
            [
                Hit("gold", MemoryType.Procedure, 10.0),
                Hit("V1", MemoryType.Insight, 8.0),
                Hit("V2", MemoryType.Insight, 6.0),
            ],
            GoldIds: Gold("gold"), K: 2),

        // ── 8. Procedure gold behind an observation-heavy lexical arm (k=2) ──
        // The baseline drops gold from the top-k ranking, but the type budget happens to reserve a
        // procedure slot that pulls gold back into the survival set — so recall lifts while survival does
        // not. A case where the two metrics diverge.
        new("recall-procedure-gold-vs-observations", AmaCapability.Recall,
            Lex:
            [
                Hit("o1", MemoryType.Observation, 10.0),
                Hit("o2", MemoryType.Observation, 9.0),
                Hit("gold", MemoryType.Procedure, 5.0),
                Hit("o4", MemoryType.Observation, 2.0),
            ],
            Vec:
            [
                Hit("gold", MemoryType.Procedure, 10.0),
                Hit("o3", MemoryType.Observation, 7.0),
            ],
            GoldIds: Gold("gold"), K: 2),

        // ── 9. Sanity anchor: gold strong in BOTH arms — baseline wins too (no lift) ──
        // Proves the benchmark isn't rigged pro-fusion: when gold is the obvious top lexical hit, the
        // flat baseline recovers it just as well.
        new("recall-gold-strong-both-arms-sanity", AmaCapability.Recall,
            Lex:
            [
                Hit("gold", MemoryType.Insight, 10.0),
                Hit("d1", MemoryType.Observation, 3.0),
            ],
            Vec:
            [
                Hit("gold", MemoryType.Insight, 10.0),
                Hit("d2", MemoryType.Insight, 2.0),
            ],
            GoldIds: Gold("gold"), K: 1),

        // ── 10. HARD: gold genuinely weak in both arms — fusion can't lift it (k=2) ──
        // Gold is near the bottom of both arms; no fusion lifts a doubly-weak candidate above stronger
        // single-arm hits. Fusion misses (recall 0, survival 0) — exactly as it should.
        new("recall-hard-gold-weak-both-arms", AmaCapability.Recall,
            Lex:
            [
                Hit("d1", MemoryType.Insight, 10.0),
                Hit("d2", MemoryType.Insight, 8.0),
                Hit("d3", MemoryType.Observation, 6.0),
                Hit("gold", MemoryType.Procedure, 2.0),
            ],
            Vec:
            [
                Hit("e1", MemoryType.Insight, 10.0),
                Hit("e2", MemoryType.Insight, 8.0),
                Hit("gold", MemoryType.Procedure, 4.0),
                Hit("e3", MemoryType.Observation, 2.0),
            ],
            GoldIds: Gold("gold"), K: 2),

        // ── 11. HARD: two same-type golds, type budget admits only one (k=2) ──
        // Both golds rank top-2 under fusion AND the baseline, but the Insight budget at k=2 is 1, so
        // survival is capped at 0.5 for every ranker. A ranker-independent ceiling — the lift is 0 here.
        new("recall-hard-budget-caps-survival", AmaCapability.Recall,
            Lex:
            [
                Hit("goldA", MemoryType.Insight, 9.0),
                Hit("goldB", MemoryType.Insight, 7.0),
                Hit("d1", MemoryType.Observation, 2.0),
            ],
            Vec:
            [
                Hit("goldA", MemoryType.Insight, 8.0),
                Hit("goldB", MemoryType.Insight, 10.0),
                Hit("v1", MemoryType.Observation, 1.0),
            ],
            GoldIds: Gold("goldA", "goldB"), K: 2),

        // ── 12. HARD: a distractor is simply more relevant than gold (k=1) ──
        // The "dCombo" distractor is combined-strong in both arms and outranks gold on the merits.
        // Fusion correctly ranks it #1, so gold misses at k=1 — fusion ranking by relevance, not bias.
        new("recall-hard-superior-distractor", AmaCapability.Recall,
            Lex:
            [
                Hit("dCombo", MemoryType.Insight, 9.0),
                Hit("gold", MemoryType.Procedure, 6.0),
                Hit("d2", MemoryType.Observation, 2.0),
            ],
            Vec:
            [
                Hit("dCombo", MemoryType.Insight, 10.0),
                Hit("gold", MemoryType.Procedure, 7.0),
                Hit("v1", MemoryType.Insight, 1.0),
            ],
            GoldIds: Gold("gold"), K: 1),

        // ── 13. GRAPH: gold reachable ONLY as a link-neighbor of a strong parent (k=2) ──
        // Gold is in NEITHER arm — fusion alone never sees it. But the top both-arms parent links to it
        // (refines), so graph expansion pulls it in with damped inheritance (parentFused·0.5 + recency),
        // outranking the single-arm distractor and landing inside k=2. The baseline never expands, so it
        // scores recall 0 on this case — a clean lift attributable purely to graph expansion.
        new("recall-graph-neighbor-rescue", AmaCapability.Recall,
            Lex:
            [
                HitLinkedTo("parent", MemoryType.Insight, 10.0, linkTo: "gold"),
                Hit("lexOnly", MemoryType.Insight, 4.0),
            ],
            Vec:
            [
                Hit("parent", MemoryType.Insight, 10.0),
                Hit("vecOnly", MemoryType.Observation, 3.0),
            ],
            GoldIds: Gold("gold"), K: 2)
        {
            Neighbors = Neighbors(("gold", MemoryType.Insight)),
        },

        // ── 14. GRAPH: link-neighbor gold of a Procedure parent, deeper pool (k=3) ──
        // A second graph rescue with a different parent type and a noisier pool: the gold sits behind a
        // procedure parent that tops both arms. Expansion inherits the parent's strength and seats gold
        // ahead of the lexical/vector noise tail; the baseline, blind to the link, misses it entirely.
        new("recall-graph-neighbor-deeper-pool", AmaCapability.Recall,
            Lex:
            [
                HitLinkedTo("proc", MemoryType.Procedure, 10.0, linkTo: "gold"),
                Hit("n1", MemoryType.Insight, 6.0),
                Hit("n2", MemoryType.Observation, 3.0),
            ],
            Vec:
            [
                Hit("proc", MemoryType.Procedure, 10.0),
                Hit("n3", MemoryType.Insight, 5.0),
            ],
            GoldIds: Gold("gold"), K: 3)
        {
            Neighbors = Neighbors(("gold", MemoryType.Heuristic)),
        },
    ];
}
