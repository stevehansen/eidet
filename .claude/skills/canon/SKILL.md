---
name: canon
description: Prime on Eidet's Canon before changing it — the propose/review/approve loop over Term (P1) and Domain (P2) pages, CanonDraft in its own canondrafts/* collection, CanonService's damper matrix and claim-before-mint protocol, ICanonMintPort as the sole write edge into canon:* memories, fingerprints, rejection cooldowns, and the canon:* guard that keeps curated pages out of dedup/consolidation. Use when the task touches a Canon draft or page, a draft source, the Web UI Canon panel, or /api/eidet/canon/*. Not for parked open work (see looseends), not for the live Portal view (see portal).
---

# Canon — priming

**Canonical spec:** `docs/domains/canon.md` — read it for the damper matrix, all invariants, key files,
and gotchas. Terms of record: `UBIQUITOUS_LANGUAGE.md` § Canon (and *Approve vs Promote*,
*Canon draft vs Loose End*).

A draft is **not** a memory until an Operator approves it. Only Term pages ship today, from two
deterministic zero-LLM sources (entity aggregation, `UBIQUITOUS_LANGUAGE.md` seed). REST/Web UI only —
no MCP surface.

## Core invariants (get these right)

- **Approve is the only write path into `canon:*`**, always via `ICanonMintPort` → `StoreAsync`. That's
  the enforcement point for the zero-LLM write path once syntheses come from a model.
- **Secret-scan draft prose at creation *and* gate again at approve** — source prose can echo a member's
  secret.
- **The draft id (`canondrafts/{repo}/{kind}/{slug}`) is the damper anchor** — one doc per slug,
  refreshed in place; identical fingerprint ⇒ regeneration does nothing.
- **Reopening a rejected draft needs both an elapsed cooldown and a changed fingerprint.** Never disturb
  a draft in `Approving`.
- **Claim before mint** (`Pending → Approving`), release to a *clean* `Pending` on failure (on
  `CancellationToken.None`), and stay idempotent — the loose-end resolve pattern.
- **Dedup and consolidation must never touch a `canon:*` page** — both filter on `CanonTags.IsCanonPage`.
- **Strip `canon:*` from inherited member tags** so a page never re-tags itself by another page's tag.
- **Provenance follows the anti-laundering rule over surviving members** — approval doesn't confer trust.
- **A forgotten member degrades to a placeholder, never a throw**; `DerivedFrom` keeps the full snapshot.

## Key files / reuse

- `src/Eidet.Core/Canon/CanonService.cs` — the loop, the damper, the claim protocol.
- `src/Eidet.Core/Canon/ICanonDraftSource.cs` — add a source here; the service never changes.
- `src/Eidet.Core/Canon/MemoryServiceCanonAdapter.cs` — the mint edge.
- `src/Eidet.Core/Canon/{CanonFingerprint,CanonTags,CanonSlug}.cs` — reuse, don't re-derive.
- `tests/Eidet.Core.Tests/Canon/TestDoubles.cs` — the in-memory doubles.

## Gotchas

- `IsStale` is hardcoded `false` (live drift detection is P2) — don't trust the flag.
- `Superseded` is vestigial for slug-keyed drafts; `Approving` never appears in REST responses.
- `RegenerateDraftsAsync` takes the repo **path**, not a normalized id — a normalized id silently
  disables file-backed sources like the UL parser.
- The UL source skips "Example dialogue"/"Flagged ambiguities" and any row without a bolded term cell.
- The entity source excludes Observations and requires several distinct citing memories.
