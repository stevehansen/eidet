---
name: writepath
description: Prime on Eidet's write path and integrity model before changing it — the always-on secret scanner, the signal/self-talk gate, WriteValidator entry construction, duplicate and conflict gates, quarantine, the poison log, derived MemoryTrust, provenance tiers, content commitments, and the IntegrityAuditor. Use when the task touches WriteValidator, SecretScanRule, ConflictGate, MemoryTrust, MemoryCommitment, ProvenanceRules, QuarantineInfo, IPoisonLog, IntegrityCheck, or any STRIDE tampering/poisoning finding. Not for the MemoryEntry shape or lifecycle verbs (see memory), not for recall ranking (see recall), not for scheduled rewrites (see maintenance).
---

# Write path & integrity — priming

**Canonical spec:** `docs/domains/writepath.md` — read it for the full gate order, all invariants, key
files, and gotchas. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Write path. Threats: `STRIDE.md` T-8,
T-13, T-15, T-20.

Deterministic, local, zero-LLM. The posture is adversarial: assume some memories are poisoned,
imported, or rewritten, and make sure they cannot pass as first-party knowledge. **memory** owns the
entity; this domain owns whether a write happens and what trust it earns.

## Core invariants (get these right)

- **Secret scanning is never optional on any write path.** `ScanSecrets` is separate from `Validate`
  so non-semantic surfaces can skip the low-signal/self-talk gates — never the secret scan.
- **`WriteValidator.BuildEntry` is the only construction path for a stored memory** (validation, id,
  defaults, provenance, entity extraction). Don't hand-build a `MemoryEntry` on a write path.
- **Trust is derived on every read, never stored** — no field to forge or let drift.
- **The commitment factor multiplies AFTER the echo lift.** Reversed, echoes launder rewritten content
  (STRIDE T-8). The only sanctioned repair for a broken commitment is supersession.
- **No insecure default in `ProvenanceTrust`** — trusted origins are enumerated, everything else
  (including `Unknown`) lands on the import floor. `Unknown` sits at *exactly* that floor because the
  pack-import clamp compares floors.
- **A synthesis inherits its least-trusted contributor** unless every contributor is trusted
  (`ProvenanceRules`) — that's the anti-laundering guarantee.
- **De-boost, never hide.** A quarantined or broken-commitment memory stays recallable so it can earn
  the echo that clears it; it raises a dashboard finding instead of disappearing.
- **A contradiction needs all three:** near-duplicate content + opposite *hard* valence signs +
  high-trust incumbent. Supersessions are exempt from the conflict gate and the poison fast-path.
- **Adding an `IntegrityCheck` without a probe throws** — and that throw is exempt from per-check
  isolation, on purpose.

## Key files / reuse

- `src/Eidet.Core/Gates/WriteValidator.cs` + `SecretScanRule.cs` — the gate chain.
- `src/Eidet.Core/Memory/MemoryTrust.cs` — the trust algebra (`Explain` for forensics).
- `src/Eidet.Core/Memory/MemoryCommitment.cs` — `Render` is the only way to authorize an in-place
  content rewrite; anything else reads as tampering.
- `src/Eidet.Core/Memory/{ConflictGate,ProvenanceRules,IPoisonLog}.cs` — write-time verdicts.
- `src/Eidet.Core/Integrity/IntegrityAuditor.cs` — one probe per read path.

## Gotchas

- The secret gate is pattern-based, not entropy-based: prose mentioning `Password=…` or a base64 blob
  ending `==` is rejected. Rephrase the memory; don't weaken the pattern.
- Stores *reject* secrets; only the memory-tool path *redacts* them.
- `BuildEditEntry` stamps `Provenance = UserStated` unconditionally — an edited agent memory becomes
  fully trusted.
- `NullPoisonLog` is the default, so the poison fast-path is inert unless the host wires the Raven log.
- A clean `IntegrityReport` is only as broad as `ChecksProbed`; capped citations and failed probes are
  unprobed, not clean.
