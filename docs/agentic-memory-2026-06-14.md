# Agentic Memory Research — 2026-06-14

A delta survey of agentic-memory work from **March–June 2026**, mapped onto Eidet.

## Why this doc exists

We seeded parts of Eidet's design from the external research repo
[`lhl/agentic-memory`](https://github.com/lhl/agentic-memory) (ENGRAM typed budgets, Zep/Graphiti
validity intervals, jumperz echo/fizzle, Mem0 ops). That repo's arxiv coverage **stops at Feb 2026
(`2602.x`)**, its last commit is April 2026, and its synthesis targets the author's own project
(`shisad`), not Eidet. This doc covers the gap (Mar–Jun 2026) and — unlike the source repo — maps
everything to **Eidet's actual code**.

### How to read the confidence/verification tags

Most individual papers here are <8-week-old, single-author, non-peer-reviewed preprints with
**self-reported** numbers. Each finding was put through an adversarial verification pass. Tags:

- **✅ confirmed** — existence + load-bearing claims corroborated by sources *independent* of the original.
- **◐ uncertain** — paper is real and accurately described, but numbers are single-source / unreplicated, or a sub-claim was wrong.
- We lean on **mechanisms and multi-source-confirmed patterns**, not headline benchmark numbers.

## TL;DR

1. **The field converged on Eidet's bets.** A near-clone of Eidet's architecture (Engram, `2606.09900`)
   was published this month; first-party memory (Anthropic Managed Agents, OpenAI Codex, ChatGPT
   Dreaming V3) all land on append-only + versioned + redactable + consolidated. Industry is now putting
   an LLM *on the write path* (Mem0) — which makes Eidet's **zero-LLM deterministic write path a
   headline differentiator**, not just an implementation detail.
2. **There's a clean, mostly-deterministic "steal list"** that fits the zero-LLM ethos — concentrated in
   recall scoring, where Eidet is currently weakest.
3. **One real hole: no published benchmark number.** Competitors publish LoCoMo/LongMemEval/BEAM;
   Eidet publishes a test count.
4. **Two genuine competitive threats** (Claude Code native auto-memory; Letta Code + local-first MCP
   memory servers) and several **interop plays** that turn competitor surfaces into distribution.

---

## 1. Validation — the field converged on Eidet's bets

| What shipped | Source | Overlap with Eidet |
|---|---|---|
| **Engram** — zero-LLM fast write + async consolidation + bi-temporal validity + supersedes chains + hybrid retrieval + local-first | `2606.09900` ◐ (solo author, ~9 days old, self-reported) | Near-identical to Eidet's entire thesis, published independently. Cite as external validation. |
| **Anthropic Managed Agents memory API** — immutable versions (`memver_`), `content_sha256` optimistic concurrency, redact-history op, "dreaming" consolidation | docs `managed-agents-2026-04-01` ✅ | Append-only + versioned + redactable + consolidate = our model, now table-stakes. |
| **OpenAI Codex Memories** — idle-gated, secret-redacting, rate-aware background consolidation | ✅ | Our consolidation + FadeMem + gates — but their write path costs tokens/rate-limit; ours is deterministic & free. |
| **ChatGPT Dreaming V3** — temporal self-rewriting ("going to X" → "went to X") | ◐ (vendor-internal numbers) | Exactly what our validity intervals + supersession express. Exposes a gap (we decay but don't *rewrite* time-relative claims). |
| **Mem0 (Apr 2026)** — single-pass LLM extraction on the write path; agent-generated facts first-class | ✅ | Industry adding LLM to write path = our zero-LLM path is differentiated. **Tension:** their "agent facts first-class" vs our self-talk filter — revisit whether we over-filter agent decisions. |

---

## 2. Retrieval & scoring — highest ROI, all deterministic

**Current state (grounded in code):** `MemoryService.RecallInternalAsync` (`MemoryService.cs:361-368`)
merges full-text and vector hits by **discarding RavenDB's relevance scores** and assigning flat
constants (`1.0` for full-text hits, `0.9` for vector-only). `ApplyTypeBudgets` then orders by that
near-constant score. So the recall path has **effectively no relevance ranking before truncation**.
The L1-context path (`RecallScoring.ComputeL1Score`) and `FadeMemCurve.Decay` both decay on
**creation-age only** (single clock); a raw `frequency = AccessCount/10` term already exists.

| Change | Source | Conf. | Maps to |
|---|---|---|---|
| **Rerank candidates by real relevance *before* the budget truncation.** SmartSearch oracle: 98.6% recall but only **22.5% of gold survives truncation** without ranking — the bottleneck is *ordering at the budget boundary*, not recall. | SmartSearch `2603.15599` | ✅ | `MemoryService.cs:361-383`, `RecallScoring.ApplyTypeBudgets` |
| **Stop flat-constant fusion; use normalized convex combination** `α·norm(lex) + (1-α)·norm(vec)` (beats rank-based RRF too). Tune α per-repo from echo/fizzle (each is a free relevance label). | Bruch et al. `2210.11934` (not new, load-bearing) | ✅ | `MemoryService.cs:363-368` |
| **UCB exploration bonus when wiring echo/fizzle into scoring.** Pure utility/frequency reweighting collapses onto "winner" memories (some pulled >15×, most starve). Add `κ·sqrt(ln N / n_i)`. The existing `AccessCount/10` term *is* the rich-get-richer risk. | RetroAgent SimUtil-UCB `2603.08561` (equations verified) | ✅ | `RecallScoring.ComputeL1Score`, feedback loop |
| **Dual-clock recency** — split FadeMem into creation-age **+ last-access-age** (refresh on recall/echo). A Procedure recalled yesterday ≠ cold. | Digital Me `2506.23826` | ✅ | `FadeMemCurve.Decay`, `RecallScoring.ComputeL1Score` |
| **Reuse the `eidet_link` graph as a retrieval signal** — expand top-k candidates with link-neighbors before rerank. Nearly free multi-hop; turns the graph from a viz artifact into retrieval. | GAR/LADR family (EACL 2026 survey) | ◐ | `eidet_link`, `/graph`, recall pipeline |
| **If query expansion is ever added, gate it** — unguarded reformulation drifts; anchor to the original query. | ReformIR `2605.00560` | ✅ (1 benchmark mislabel) | future recall feature |

→ **Issue: #33** (epic).

---

## 3. Coding-procedural — our actual domain

| Finding | Source | Conf. | Takeaway |
|---|---|---|---|
| **Procedure ROI.** Of 49 real SWE skills: **39 gave zero gain, avg +1.2%, 3 actively degraded** (stale/version-mismatched), token overhead −78% to +451%. A wrongly-recalled procedure is **net-negative, not neutral**. | SWE-Skills-Bench `2603.15401` | ✅ (corroborated by SkillsBench `2602.12670`) | Track realized benefit per recalled Procedure; auto-demote zero/negative-ROI via FadeMem; cap procedures-per-wake-up; version drift = first-class fizzle. → **#35** |
| **Functional-stage hard pre-filter** — tag `{analyze, locate, edit, test, debug}`, AND-filter before vector rank (+4.7pp mean SWE-bench Verified). Store at subtask granularity. | Subtask-Level Memory `2602.21611` | ✅ | Fits composite-index + AND-semantics. → **#38** |
| **Applicability conditions** — explicit "when does this apply / done" fields on procedures (machine-checkable, improves recall precision). | Skill-Pro `2602.01869` | ◐ | → **#38** |
| **Git-history intake** — merged commits/PRs are *already-human-verified* intent→code mappings; a local, zero-network seed source, summarizable via existing Ollama enrichment. | MemCoder `2603.13258` ◐, Lore `2603.15566` | ◐ | Complements `eidet_intake`; nuances our "don't store git-derivable" rule (harvest the *valuable subset* once). → **#40** |
| **Two-altitude storage + cross-model portability** — keep fine-grained steps *and* a script-like abstraction; procedures built by a strong model transfer to weak models. | Memp `2508.06433` | ✅ (multi-source) | Direct argument for model-agnostic markdown packs + Shared/Base layers. → **#39** |

---

## 4. Forgetting & consolidation

| Change | Source | Conf. | Takeaway |
|---|---|---|---|
| **Budgeted forgetting** — usage-reinforcement term (from echo) + per-repo/type **budget eviction**, vs decay-to-irrelevance alone. | `2604.02280` | ◐ (exists-confirmed) | → **#39** |
| **Keep consolidation conservative; make embedding-dedup the primary lever.** The Human-Inspired Arch paper's honest finding: dedup does the real work and *aggressive* observation→insight merging **regressed** LongMemEval. | `2605.08538` | ◐ (single-source, but instructive) | Don't over-merge. → **#39** |
| **Two-altitude procedure output** in consolidation (see §3, Memp). | `2508.06433` | ✅ | → **#39** |
| ("Maturation ramp" for new memories is in `2605.08538` but the authors admit it's mostly inert — **skip / experimental only**.) | | | |

---

## 5. Security — we're unusually well-positioned; close 3 gaps

The **Mnemonic Sovereignty survey** (`2604.16548` ✅, multi-source) explicitly names
**write-gate validation + post-deletion verification as field-wide blind spots** — i.e. our headline
feature is the under-researched high-value spot. But our current gates (secrets + signal + self-talk)
miss the realistic threat:

| Threat / defense | Source | Conf. | Takeaway |
|---|---|---|---|
| **MemoryGraft** — single-shot, *trigger-free* poisoning via a benign-looking imported doc carrying "validated best practices." Secret-free, high-signal, plausible → passes every current gate. Exact match to our pack-import + Procedure/Heuristic surface. | `2512.16962` | ✅ (sweep-confident) | **Add a provenance/trust tier per memory**; gate retrieval weight by it; carry provenance *through consolidation* so a low-trust observation can't be laundered into a trusted insight. → **#34** |
| **A-MemGuard** — consensus/contradiction check + a "lessons" store. LLM poison detectors miss **~66%** of poison → deterministic/structural defense is correct (validates our approach). | `2510.02373` | ✅ (multi-source) | Conflict-check on Heuristic/Insight writes: recall top-k, quarantine a contradicting write pending feedback. → **#37** |
| **FAMA / Memora** — existing systems silently reuse stale/invalidated memory; metric rewards correct forgetting. | `2604.20006` | ◐ | **Post-forget verification:** assert a forgotten/superseded memory never resurfaces in recall, context (L0/L1), *or* cross-repo. Doubles as a correctness test of our validity intervals. → **#37** |

Update `STRIDE.md` with the 6-phase × 4-objective lifecycle taxonomy from `2604.16548`.

---

## 6. The one real hole — no published benchmark

Competitors publish numbers (agentmemory: 95.2% R@5 LongMemEval-S; Mem0/Supermemory: LoCoMo/BEAM).
Eidet ships "646 tests."

- **Primary target: SWE Context Bench** (`2602.08316` ✅, multi-source) — *the* coding-memory
  benchmark: 1,476 linked SWE tasks measuring cross-task experience reuse, and it **already benchmarks
  Mem0/Supermemory/OpenViking head-to-head** on resolution rate + token cost + runtime. An Eidet row
  drops into an existing leaderboard. (Head-to-head runs on the Lite subset; check for a data release.)
- **Internal scorecard: AMA-Bench** (`2602.22769` ✅, ICML 2026) — 4-capability taxonomy
  (Recall / Causal Inference / **State Updating** / State Abstraction); State Updating directly tests
  our validity-interval/forget machinery.
- **Cheapest local A/B now:** the Stompy harness (MCP-memory vs CLAUDE.md vs none) — but its quality
  numbers are unreliable (verification found the README inverts its own data; real finding is "memory
  makes agents *cheaper*, not smarter"). Directional only.

→ **#36**.

---

## 7. Competitive landscape & interop

**Threats**

- **Claude Code native auto-memory** (on by default since v2.1.59) erodes the solo-dev "remember this
  repo" use case — but it's a flat markdown index (no semantic recall, no cross-repo, no typed budgets)
  and has **no native secret redaction** (documented security issue) → positioning win for us.
- **Direct competitors in our niche:** Letta Code (memory-first coding agent, git-backed MemFS, local
  mode — ✅), and local-first MCP servers **mcp-memory-service** + **agentmemory** (✅ both real/shipped;
  typed memory, consolidation, hybrid search, Claude Code integration).

**Lead differentiation with:** deterministic always-on write gates (free-form git-file memory writes
secrets/noise straight in), zero-LLM write path, read-only Shared/Base layer + packs, harness-agnostic
via MCP.

**Interop — turn competitor surfaces into distribution** (→ **#41**):

- Implement the **Claude memory tool** (`memory_20250818`) backend (client-side/pluggable; a .NET SDK
  base class exists) → Eidet serves the entire Claude API surface, not just MCP clients.
- **AGENTS.md** as ingest + export (it's markdown; our packs already are) → Eidet becomes the cross-tool
  layer above Codex/Cursor/Gemini.
- **Import Claude Code's `MEMORY.md`** as seed → strictly additive, not "choose Eidet instead."
- Add guidance to call `eidet_store` at **compaction/context-clear boundaries** → Eidet is the durable
  layer that survives eviction.

**Watch item:** git-native memory is a real (if over-hyped) trend for coding agents; our pack is an
*export*, not a git-tracked live store. A git-materialized Local layer would give diff/blame/rollback/
PR-review of memory for free.

**Cheap curation hardening** (→ **#41**): adopt Anthropic's `content_sha256` optimistic
concurrency on the versioned `PUT`, and a redact-historical-version op (scrub content, keep the audit
node) for GDPR/secret cleanup.

---

## 8. Prioritized backlog → issues

| Priority | Issue | Theme | Effort |
|---|---|---|---|
| **P0** | #33 — Recall pipeline v2 (real fusion + rerank-before-truncate + dual-clock + UCB + graph-expand) | Retrieval | M–L |
| **P0** | #34 — Memory provenance/trust tier (+ carry through consolidation) | Security | M |
| **P1** | #35 — Procedure ROI tracking + auto-demotion | Coding | M |
| **P1** | #36 — Stand up a retrieval/resolution benchmark | Eval | M–L |
| **P1** | #37 — Post-forget verification + write-time conflict-check | Security | S–M |
| **P2** | #38 — Functional-stage hard pre-filter + applicability conditions | Coding | M |
| **P2** | #39 — Budgeted forgetting + conservative/dedup-first consolidation + two-altitude | Maintenance | M |
| **P2** | #40 — Git-history intake mode | Intake | M |
| **P3** | #41 — Claude memory-tool backend + AGENTS.md + import MEMORY.md + content_sha256/redact | Interop | L |

## Appendix — sources (arxiv id → verdict)

- `2606.09900` Engram — ◐ uncertain (solo, self-reported)
- `2604.16548` Mnemonic Sovereignty survey — ✅ confirmed
- `2604.20006` FAMA/Memora — ◐
- `2605.08538` Human-Inspired Memory Arch — ◐ (mechanisms mostly inert per authors)
- `2604.02280` Budgeted forgetting — ◐ (exists-confirmed)
- `2602.08316` SWE Context Bench — ✅ confirmed
- `2602.22769` AMA-Bench — ✅ confirmed (ICML 2026)
- `2603.15401` SWE-Skills-Bench — ✅ confirmed
- `2602.12670` SkillsBench — ✅ (independent corroboration)
- `2602.21611` Subtask-Level Memory — ✅ confirmed
- `2603.13258` MemCoder — ◐ (date mislabeled; numbers self-reported)
- `2603.15566` Lore — (git-trailers knowledge protocol)
- `2508.06433` Memp — ✅ confirmed (multi-source)
- `2602.01869` Skill-Pro — ◐
- `2603.15599` SmartSearch — ✅ confirmed
- `2603.08561` RetroAgent SimUtil-UCB — ✅ confirmed (equations verified)
- `2210.11934` Bruch convex-combination fusion — ✅ (not new)
- `2506.23826` Digital Me (dual-clock recency) — ✅
- `2605.00560` ReformIR — ✅ (one benchmark mislabel)
- `2512.16962` MemoryGraft — (poisoning threat model)
- `2510.02373` A-MemGuard — ✅ confirmed (multi-source)
- `2511.21730` Procedural Memory Retrieval bench — ✅ confirmed
- First-party: Anthropic Managed Agents (`managed-agents-2026-04-01`), Claude memory tool
  (`memory_20250818`), OpenAI Codex Memories, ChatGPT Dreaming V3 — see §1, §7.

*Research method: 7-angle parallel web sweep + adversarial verification (34 agents). Full transcript
retained in the session that produced this doc.*
