# Write path & integrity

Every check a store passes before it becomes a Memory, the trust that store earns, and the runtime
verification that the store still deserves it.

**Status:** current as of [#80](https://github.com/stevehansen/eidet/issues/80) (closed-world
provenance + content commitments) · **Governing issues:**
[#37](https://github.com/stevehansen/eidet/issues/37) (conflict gate / quarantine / poison log),
[#34](https://github.com/stevehansen/eidet/issues/34) (provenance & trust tier),
[#80](https://github.com/stevehansen/eidet/issues/80) (commitments, closed-world provenance).
Threats: `STRIDE.md` T-8, T-13, T-15, T-20.
**Priming skill:** [`.claude/skills/writepath/SKILL.md`](../../.claude/skills/writepath/SKILL.md)

## What it is

The gauntlet on the way in — deterministic, local, zero-LLM — plus the derived trust model and the
integrity auditor that keeps a trust *claim* honest at read time. Its job is adversarial: assume some
memories are poisoned, imported, or rewritten, and make sure they cannot pass themselves off as
first-party knowledge.

It is *not* the entity being written (**memory** owns `MemoryEntry` and the mutation funnel), *not*
recall's ranking arithmetic (**recall** consumes the trust factor but doesn't define it), and *not*
the background passes that later merge or decay entries (**maintenance**).

## Core entities & relationships

Two independent chains, both keyed off a store attempt:

```
StoreAsync
  ├─ WriteValidator.BuildEntry ── SecretScanRule (always-on) → signal/self-talk gate
  │                            └─ id + entities + one-liner + ProvenanceResolver + confidence floor
  ├─ IPoisonLog.MatchAsync ................. repeat contradiction → Rejected before any query
  ├─ FindDuplicateAsync + ValencePolarity .. near-dup dedup, with the polarity escape
  ├─ ConflictGate.Check .................... near-dup + opposite hard stance + high-trust incumbent
  │     └─ QuarantineInfo on the (still stored) entry + IPoisonLog.RecordAsync
  └─ hooks + supersession + store         → StoreResult { Stored | Duplicate | Rejected | QuarantinedPending }

read time:  MemoryTrust.Factor(entry) = min(provenance, type) floor
                                        ↑ echo lift  → × MemoryCommitment factor
            IntegrityAuditor.VerifyAsync(repo) → IntegrityReport (one probe per IntegrityCheck)
```

A supersession is exempt from the poison fast-path and the conflict gate — contradicting the
incumbent is a correction's whole purpose.

## Invariants & rules

- **Secret scanning is never optional on any write path.** `WriteValidator.ScanSecrets` is exposed
  separately from `Validate` precisely so surfaces that store non-semantic content can skip the
  *semantic* gates (low-signal, self-talk) while still scanning — memory-tool file writes and Canon
  draft creation both do exactly that. Owned by `src/Eidet.Core/Gates/SecretScanRule.cs`.
- **`WriteValidator.BuildEntry` is the single canonical construction path for a stored memory** —
  validation, id minting, default fields, provenance resolution, and entity extraction live in one
  place so no mutation path can bypass one of them. `BuildEditEntry` is its supersession twin.
- **Trust is derived on every read and never stored.** There is no trust field to forge, lie about,
  or let drift out of sync with the evidence. Owned by `src/Eidet.Core/Memory/MemoryTrust.cs`.
- **The commitment factor multiplies *after* the echo lift.** Echoes may rehabilitate an unknown
  origin, but must never launder content rewritten out from under its own id commitment — the only
  sanctioned repair for that is supersession, which mints a fresh id. Reversing the two silently
  reopens the laundering hole (STRIDE T-8).
- **`ProvenanceTrust` has no fallback to full trust.** Trusted origins are enumerated explicitly;
  `Unknown` and any undefined ordinal that slips past the deserializer's closed-world guard land on
  the import floor. `Unknown` sits at *exactly* the import floor, not a third tier, because the
  pack-import clamp compares floors.
- **A synthesis is only born fully trusted when every contributor was trusted.** One untrusted
  contributor demotes the emission to the least-trusted contributor's provenance, so
  compression cannot amplify a poisoned import. `Unknown` deliberately fails the trusted test.
  Owned by `src/Eidet.Core/Memory/ProvenanceRules.cs`.
- **Trust is a de-boost, never a cutoff.** Even a broken commitment or an active quarantine stays
  recallable — hiding it causes cold-start starvation and denies a false positive the chance to earn
  the echo that clears it. It raises a dashboard finding instead of disappearing (#37,
  downrank-never-hide).
- **A contradiction requires all three signals: near-duplicate content, opposite *hard* valence
  signs, and a high-trust incumbent.** Neutral and Cautionary have sign 0 and therefore never
  conflict, which bounds false positives to explicit opposite-stance pairs. Zero-LLM, and it reuses
  the neighbours the write path already fetched. Owned by `src/Eidet.Core/Memory/ConflictGate.cs`.
- **Adding an `IntegrityCheck` value without a probe fails loudly.** The auditor's dispatch throws
  `NotSupportedException` on an unmapped check, and that exception is deliberately excluded from the
  per-check isolation, so integrity coverage can never silently narrow.

## Key files

| File | Role |
|---|---|
| `src/Eidet.Core/Gates/WriteValidator.cs` | The gate chain + both entry-construction paths |
| `src/Eidet.Core/Gates/SecretScanRule.cs` | The always-on credential patterns; `Check` (reject) and `Redact` (rewrite) |
| `src/Eidet.Core/Gates/ValidationResult.cs` | Pass/fail with the gate name that rejected |
| `src/Eidet.Core/Memory/MemoryTrust.cs` | The trust algebra + `TrustBreakdown` forensics |
| `src/Eidet.Core/Memory/MemoryCommitment.cs` | Intact / Amended / Broken, and `Render` — the only way to authorize an in-place rewrite |
| `src/Eidet.Core/Memory/{ProvenanceResolver,ProvenanceRules}.cs` | Source → provenance, and the anti-laundering rule for syntheses |
| `src/Eidet.Core/Memory/ConflictGate.cs` | The write-time contradiction rule |
| `src/Eidet.Core/Memory/IPoisonLog.cs` + `src/Eidet.Core/Storage/RavenPoisonLog.cs` | Append-only contradiction log + its content fingerprint |
| `src/Eidet.Core/Domain/{QuarantineInfo,PoisonPattern}.cs` | The stored verdict and the logged pattern |
| `src/Eidet.Core/Integrity/{IntegrityAuditor,IntegrityAudit}.cs` | Runtime probes over every read path + the report model |

## Gotchas

- **The secret gate matches patterns, it does not score entropy.** Prose that merely *mentions*
  `Password=…`, a 40-char base64 blob ending in `==`, or `API_KEY: …` is rejected outright. That is
  the intended trade (never leak > never annoy), but it means a memory *about* credential handling can
  be unstoreable — rephrase rather than weakening the pattern.
- **The store path rejects secrets; only the memory-tool path redacts them.** `RedactSecrets` exists
  for byte-exact file content that must survive the write (**memorytool**); a semantic store never
  silently rewrites content.
- **`BuildEditEntry` stamps `Provenance = UserStated` unconditionally.** An agent-inferred memory
  edited through the curation API comes back as user-stated — and therefore fully trusted. Correct
  for a human edit, worth knowing before wiring any *automated* caller into the edit path.
- **`Unknown` provenance is a finding, not a state to tolerate.** The auditor reports every live
  memory with it, and it costs half the trust floor. If a new write path leaves it unset, the corpus
  quietly demotes.
- **`NullPoisonLog` is the default**, so the poison fast-path is a no-op unless the host wires the
  Raven-backed log. Zero overhead by design — but don't assume the fast-path is active in a test.
- **A citation target beyond the auditor's resolve cap is *unprobed*, not clean.** Same for a check
  whose probe threw: it is recorded as a probe failure and omitted from `ChecksProbed`. Read
  `ChecksProbed` before reading a clean report as a clean corpus.

## Executable references

- `tests/Eidet.Core.Tests/Gates/WriteValidatorTests.cs` — **the authority on the gate chain**:
  per-pattern secret hits, the low-signal and self-talk rejections, that `ScanSecrets` alone passes
  content the signal gate would reject, and that the `[REDACTED:…]` marker is itself clean.
- `tests/Eidet.Core.Tests/Memory/MemoryTrustTests.cs` — settles the floors, the echo lift, and the
  ordering that stops echoes from laundering a broken commitment.
- `tests/Eidet.Core.Tests/Memory/ProvenanceRulesTests.cs` — settles the anti-laundering rule,
  including `Unknown` failing the trusted test and the vacuous empty-contributor case.
- `tests/Eidet.Core.Tests/Memory/ConflictGateTests.cs` +
  `tests/Eidet.Core.Tests/Services/ConflictQuarantineTests.cs` — the three-signal rule, quarantine
  staying recallable, the poison fast-path, and an echo clearing the verdict.
- `tests/Eidet.Core.Tests/Integrity/IntegrityAuditorTests.cs` +
  `IntegrityIsolationTests.cs` — the coverage guard (every `IntegrityCheck` probed, unmapped values
  throwing) and per-check isolation.
- `tests/Eidet.Benchmark.Tests/FamaForgetTests.cs` — the per-memory "invalidated stays gone"
  predicate the auditor's stale-sample probes broaden.

## Links

- Glossary: `UBIQUITOUS_LANGUAGE.md` § Write path (Write gate, Secret scanner, Signal gate),
  Memory core (Provenance, Confidence)
- Threat model: `STRIDE.md` — this domain implements most of the Tampering mitigations
- Design rationale: [`docs/specs/CoreSpec.md`](../specs/CoreSpec.md) § write gates
- Related domains: **memory** (what gets written, and the mutation funnel) · **recall** (consumes the
  trust factor) · **maintenance** (`ForgetIntegrityStage` runs the auditor nightly) · **quality** (the
  dashboard that surfaces integrity findings) · **memorytool** (secret-scans, then redacts)
- Priming skill: `.claude/skills/writepath/SKILL.md`
