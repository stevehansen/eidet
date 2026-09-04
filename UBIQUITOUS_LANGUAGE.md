# Ubiquitous Language

## Memory core

| Term             | Definition                                                                                           | Aliases to avoid                       |
| ---------------- | ---------------------------------------------------------------------------------------------------- | -------------------------------------- |
| **Memory**       | A single persisted unit of agent knowledge, typed and classified, with a deterministic ID            | Entry, record, note, document          |
| **Content**      | The full prose body of a **Memory** as originally written                                            | Body, text, payload                    |
| **Summary**      | A 1-2 sentence condensation of a **Memory**, produced by **Enrichment**                              | Abstract, description                  |
| **One-liner**    | An ultra-compact (~10 word) restatement of a **Memory**, used to dense-pack **L1 Context**           | Headline, tagline                      |
| **Entity**       | A concrete named thing extracted from **Content** (file path, class, command, identifier)            | Keyword, token, reference              |
| **Tag**          | A free-form label attached to a **Memory** to aid **Recall** and browsing                            | Category, label, topic                 |
| **Importance**   | An operator-set 0–1 weight expressing how much this **Memory** should influence **Recall**           | Weight, priority                       |
| **Confidence**   | A 0–1 weight expressing how certain we are the **Memory** is true; distinct from **Importance**      | Certainty, trust                       |
| **Provenance**   | Where a **Memory** came from: user-stated, agent-inferred, tool output, consolidation, intake, etc.  | Origin, author                         |

## Memory types

Every **Memory** has exactly one of the following types. Each type has its own retrieval budget in **Recall**.

| Term             | Definition                                                                                           | Aliases to avoid                       |
| ---------------- | ---------------------------------------------------------------------------------------------------- | -------------------------------------- |
| **Observation**  | A raw fact, event, or decision captured from a session; the most volatile memory type                | Note, log, finding                     |
| **Insight**      | Stable, confirmed knowledge — usually produced by **Consolidation** of multiple **Observations**     | Conclusion, lesson                     |
| **Procedure**    | A multi-step workflow or recipe for accomplishing a concrete task                                    | Runbook, how-to, script                |
| **Heuristic**    | A do/don't rule of thumb distilled from experience                                                   | Rule, guideline, best practice         |

## Namespacing & layers

| Term             | Definition                                                                                           | Aliases to avoid                       |
| ---------------- | ---------------------------------------------------------------------------------------------------- | -------------------------------------- |
| **Repo**         | A namespace for **Memories**, identified by a normalized filesystem path (e.g. `P--Eidet`)           | Project, workspace, namespace          |
| **Layer**        | A read-only or read-write container of **Memories** that can be stacked, Docker-style                | Collection, pool                       |
| **Local layer**  | The single read-write **Layer**; all new writes land here                                            | Default layer, working layer           |
| **Shared layer** | A read-only **Layer** imported from a team or author; contributes to **Recall** but not writes       | Team layer, external layer             |
| **Base layer**   | A read-only **Layer** shipped by a package or framework author                                       | System layer, vendor layer             |
| **Link**         | A typed relation from one **Memory** (or **Repo**) to another, possibly across **Repos**             | Reference, edge, pointer               |

## Retrieval & context loading

| Term             | Definition                                                                                           | Aliases to avoid                       |
| ---------------- | ---------------------------------------------------------------------------------------------------- | -------------------------------------- |
| **Recall**       | A single hybrid query (vector + full-text + filters) that returns ranked **Memories**                | Search, lookup, query                  |
| **Context**      | A compact bundle of **Memories** packed for injection into an agent's prompt                         | Prompt context, preamble               |
| **L0**           | ~50-token identity layer: repo name, role, non-negotiables; always loaded at session start           | Tier 0, header                         |
| **L1**           | ~500-token top-ranked **One-liners**; loaded at session start for <600 token wake-up                 | Tier 1, hot set                        |
| **L2**           | On-demand deep **Recall** — full **Content** pulled only when the agent asks                         | Tier 2, cold set                       |
| **Foresight hint** | A predictive relevance signal attached to a **Memory**, nudging **Recall** in related situations   | Hint, prediction                       |
| **Cross-repo**   | A **Recall** mode that also searches linked **Repos**; off by default                                | Global search, federated search        |
| **Arm**          | One independently-scored retrieval channel feeding **Fusion**: *lexical*, *vector*, or *abstraction* | Strategy, retriever, index             |
| **Abstraction**  | A **Memory**'s shortest faithful self-description — the **One-liner** if it has one, else the **Summary**, else its **Content**, clamped. Derived at index time and embedded on its own, so what a **Memory** *is about* is not outvoted by a long body | Title, gist, headline |
| **Fusion**       | Combining the **Arms** into one ranked candidate pool: min-max normalize each, blend lexical vs vector by the learned alpha, add the **Abstraction** arm, **UCB**, and recency | Merge, blending, RRF |
| **Expansion**    | Admitting **Memories** no **Arm** returned, by reachability from the top candidates, at a damped score. Two paths: *graph expansion* (authored **Links**) and *cue expansion* (shared **Cue anchors**) | Traversal, walk, hop |
| **Cue anchor**   | An **Entity** on a **Memory** used as a retrieval handle: two **Memories** sharing one are reachable from each other without any authored **Link** | Tag, keyword, facet |

## Feedback & scoring

| Term             | Definition                                                                                           | Aliases to avoid                       |
| ---------------- | ---------------------------------------------------------------------------------------------------- | -------------------------------------- |
| **Echo**         | Positive feedback: a recalled **Memory** was useful; boosts future **Recall** score                  | Upvote, thumbs-up                      |
| **Fizzle**       | Negative feedback: a recalled **Memory** was irrelevant; dampens future **Recall** score             | Downvote, reject                       |
| **FadeMem**      | The decay model that reduces a **Memory**'s influence over time unless reinforced by **Echoes**      | Decay, aging                           |
| **ROI**          | Realized net benefit of an action-shaped **Memory** (**Procedure**/**Heuristic**), derived from **Echoes** minus **Fizzles**; net-negative **ROI** demotes the **Memory** at **Recall** and via **FadeMem**. Reversible; distinct from **Forget** (no soft-delete) | Value, payoff, utility |
| **Fizzle reason** | Optional taxonomy on a **Fizzle** (WrongContext / Incorrect / VersionDrift / Other); content-invalidating reasons (VersionDrift, Incorrect) penalize harder | Fizzle category, downvote reason |
| **Access count** | Number of times a **Memory** has been surfaced by **Recall**                                         | Views, hits                            |

## Write path

Every store attempt passes through these gates, in order. Any gate can reject the write.

| Term               | Definition                                                                                         | Aliases to avoid                       |
| ------------------ | -------------------------------------------------------------------------------------------------- | -------------------------------------- |
| **Write gate**     | A deterministic, local, always-on check that runs before a **Memory** is persisted                 | Filter, validator                      |
| **Secret scanner** | The **Write gate** that rejects **Content** matching credential patterns (13 built-in)             | Credential filter, secret filter       |
| **Signal gate**    | The **Write gate** that rejects low-signal, trivially-derivable **Content**                        | Noise filter, quality gate             |
| **Enrichment**     | Optional model-backed post-write step that generates **Summary**, **One-liner**, **Foresight hint** | Summarization, augmentation          |
| **Enrichment backend** | One model server **Enrichment** talks to — Ollama or any OpenAI-compatible endpoint, local or a private network cluster. The primary plus its ordered **fallbacks** form a chain tried in order per call | Provider (alone), LLM, endpoint |

## Lifecycle

| Term               | Definition                                                                                         | Aliases to avoid                       |
| ------------------ | -------------------------------------------------------------------------------------------------- | -------------------------------------- |
| **Validity**       | A `ValidFrom`/`ValidUntil` interval attached to every **Memory**; drives point-in-time queries     | Lifetime, window                       |
| **Supersession**   | A content edit produces a new **Memory** whose `ParentMemoryId` points at the old one              | Replacement, update, overwrite         |
| **Version chain**  | The linked list of superseded **Memories** ending at the latest version                            | History, revision chain                |
| **Forget**         | A soft-delete that records a reason and closes the **Validity** interval; never hard-deletes       | Delete, purge, remove                  |
| **TTL expiry**     | A scheduled **Forget** driven by `ForgetAfter` on a **Memory**                                     | Expiration, timeout                    |
| **Consolidation**  | A scheduled pass that merges related **Observations** into stable **Insights**                     | Compaction, rollup, summarization      |
| **Lineage**        | The `DerivedFrom` set recording which **Memories** a synthesis was built from. Read across the whole history, retired members included — "these sources were already consolidated" is a fact retiring a **Memory** cannot undo | Sources, parents, provenance (reserved) |
| **Maintenance**    | The periodic pipeline that runs **TTL expiry**, dedup, **FadeMem** decay, and **Enrichment**       | Housekeeping, cleanup, cron            |
| **Maintenance run** | One execution of the **Maintenance** pipeline over one repo. At most one is in flight per repo, whoever asked; a REST caller that outwaits the grace window gets a **run id** to poll while it continues | Job, task, sweep, pass                 |
| **Intake**         | Bulk ingestion of project files (CLAUDE.md, README, docs) as seed **Memories**                     | Import, bootstrap, seeding             |
| **Git-History Intake** | **Intake** that mines merged commit history into seed Procedure/Insight **Memories** — problem from the message, fix pattern from change stats, never raw diffs | Git import, commit harvesting          |
| **Watermark**      | Per-repo cursor (`GitIntakeLastSha`) marking the newest commit a **Git-History Intake** run processed; the next run resumes past it | Checkpoint, cursor, marker             |

## Loose End lifecycle

Open work an **Agent** defers mid-task — distinct from a **Memory** (recalled knowledge; see Flagged ambiguities). A **Loose End** lives in its own store, is exempt from **FadeMem** decay and **Consolidation**, and keeps surfacing in **Context** until explicitly closed.

| Term                | Definition                                                                                                          | Aliases to avoid                  |
| ------------------- | ------------------------------------------------------------------------------------------------------------------- | --------------------------------- |
| **Loose End**       | A deferred, still-actionable note an **Agent** parks mid-task to pick up later (a suspected bug, out-of-scope work)  | Todo, Task, Ticket, Parked memory |
| **Park**            | The low-friction act of capturing a **Loose End**; accepts terse, speculative phrasing the **Signal gate** would reject | Stash, jot, note                  |
| **Open**            | The state of a **Loose End** that still needs action; it keeps surfacing until resolved                             | Pending, active                   |
| **Resolve**         | Explicitly closing a **Loose End** with a **Resolution kind**; distinct from **Forget**, **TTL expiry**, and **Supersession** | Close, finish, complete           |
| **Resolved**        | The terminal state of a **Loose End**, carrying a **Resolution kind**                                               | Closed                            |
| **Resolution kind** | Why/how a **Loose End** closed: **Done**, **Dropped**, **Promoted**, or **Superseded**                              | Status, outcome                   |
| **Promote**         | Resolving a **Loose End** by graduating its substance into a **Memory** (**Observation**/**Insight**) or a linked external issue | Convert, save                     |

## Memory-tool files

Claude's `memory_20250818` scratch space served by Eidet — distinct from a **Memory** (knowledge) and a **Loose End** (open work). A **Memory file** is a faithful, byte-exact blob the model re-reads verbatim; it bypasses the **Signal gate**, **FadeMem** decay, and **Consolidation** by design, but never the **Secret scanner**.

| Term               | Definition                                                                                                                | Aliases to avoid                       |
| ------------------ | -------------------------------------------------------------------------------------------------------------------------- | -------------------------------------- |
| **Memory file**    | A byte-exact, path-keyed, overwrite-in-place blob under `/memories`, stored per-repo in its own `memoryfiles/*` collection | Memory, note, document                 |
| **Memory tool**    | Claude's `memory_20250818` command set (view/create/str_replace/insert/delete/rename) that Eidet serves as a backend       | Memory API, file tool                  |
| **Translator**     | The single-entry Core module that executes memory-tool commands over **Memory files**; never rewrites bytes semantically   | Handler, dispatcher                    |
| **Bridge**         | The opt-in, one-way shadow that promotes a written **Memory file** into a **Memory** through the full **Write gate** (off by default) | Sync, mirror, projection |

## Canon (curated knowledge base)

The human-approved subset of a **Repo**'s **Memories**, structured as domain and glossary pages — distinct from the store as a whole (all knowledge) and from the **Portal** (a live view). A **Canon page** *is* a **Memory** and participates in **Recall**; a **Canon draft** is not, and lives in its own `canondrafts/*` collection until an **Operator** **Approves** it.

| Term             | Definition                                                                                              | Aliases to avoid                       |
| ---------------- | -------------------------------------------------------------------------------------------------------- | -------------------------------------- |
| **Canon**        | The human-approved subset of **Memories** (**Domain** and **Term** pages) that renders as the repo's knowledge base | KB, wiki, docs                         |
| **Canon page**   | A **Memory** holding an **Approved** synthesis; `type=Insight`, `canon:*` tagged, `derivedFrom` = members | Concept doc, article                   |
| **Domain**       | A **Canon page** synthesizing a tag-defined cluster of **Memories**; its own tags declare the membership | Topic, area, cluster                   |
| **Term**         | A **Canon page** defining one glossary entry (an **Entity** or authored term)                            | Glossary entry, definition             |
| **Canon draft**  | An LLM-proposed (or UL-seeded) synthesis awaiting **Operator** review; never a **Memory** until **Approved** | Proposal, pending page                 |
| **Approve**      | The **Operator** act graduating a **Canon draft** into a **Canon page** through the full **Write gate**  | Promote (reserved for **Loose Ends**), accept, merge |

## Sharing

| Term               | Definition                                                                                         | Aliases to avoid                       |
| ------------------ | -------------------------------------------------------------------------------------------------- | -------------------------------------- |
| **Pack**           | A shareable markdown bundle of **Memories** with YAML frontmatter; renders anywhere                | Bundle, archive, export                |
| **Pack export**    | Producing a **Pack** from one or more **Layers**                                                   | Dump, snapshot                         |
| **Pack import**    | Consuming a **Pack**; auto-mounts it as a **Shared layer**                                         | Load, install                          |
| **ScribeGate**     | The external pack-format contract Eidet's **Pack** is compatible with                              | (proper name)                          |

## Actors & surfaces

| Term               | Definition                                                                                         | Aliases to avoid                       |
| ------------------ | -------------------------------------------------------------------------------------------------- | -------------------------------------- |
| **Agent**          | An AI coding assistant that reads/writes **Memories** via MCP or REST                              | Bot, assistant, client                 |
| **Session**        | A single agent run; identified by `SourceSessionId` on **Observations** it creates                 | Conversation, run                      |
| **Operator**       | A human who curates **Memories** via Web UI, CLI, or API (PUT/forget/edit)                         | Maintainer, user                       |
| **Service**        | The Eidet daemon process that hosts MCP, REST, scheduler, and Web UI                               | Server, daemon, host                   |
| **Hook**           | A pre- or post-lifecycle shell command the **Service** invokes on one of six events                | Callback, trigger                      |

## Relationships

- A **Memory** belongs to exactly one **Repo** and exactly one **Layer**.
- Only the **Local layer** accepts writes; **Shared** and **Base layers** are read-only but contribute to **Recall**.
- Each **Memory** has exactly one **Memory type**, and each type has its own budget in **Context** assembly.
- A **Supersession** creates a new **Memory** linked to its parent — the original is never mutated.
- **Forget** closes a **Memory**'s **Validity** window but preserves the document for audit.
- **Consolidation** produces **Insights** derived from multiple **Observations**; the **Observations** remain.
- **Echo** and **Fizzle** adjust **Recall** ranking but do not alter **Content**.
- A **Pack import** mounts a **Shared layer**; it never merges into the **Local layer**.
- A **Loose End** belongs to one **Repo** and the **Local layer** only; it is never written to a **Pack** or **Shared layer**.
- A **Loose End** stays **Open** until an **Agent** **Resolves** it; nothing closes it automatically — unlike a **Memory**, it does not **FadeMem**-decay or undergo **TTL expiry**.
- **Resolving** a **Loose End** as **Promoted** produces a **Memory**; **Resolving** it as **Dropped** is *not* the same as **Forget** (which retires a **Memory**).
- A **Canon page** is a **Memory** and participates in **Recall**; a **Canon draft** is not — it lives in `canondrafts/*` until **Approved**, and **Approve** is the *only* write path into `canon:*`.
- **Consolidation** and dedup skip **Canon pages** — they are already human-approved syntheses.

## Example dialogue

> **Dev:** "If the agent learns something during a **Session**, does it go straight in as an **Insight**?"

> **Domain expert:** "No — it enters as an **Observation** in the **Local layer**. **Insights** come out of **Consolidation**, once we've seen the pattern enough times and with enough **Confidence**."

> **Dev:** "What stops the agent from writing a secret into a **Memory**?"

> **Domain expert:** "The **Secret scanner** **Write gate**. Every store passes through it before the **Memory** is persisted. If the **Content** matches a credential pattern, the write is rejected. The **Signal gate** runs right after and rejects low-signal noise."

> **Dev:** "Say a teammate's **Pack** contains an **Insight** that turns out to be wrong here. Can I fix it?"

> **Domain expert:** "Not in place — a **Pack import** lands in a **Shared layer**, which is read-only. You **Forget** it with a reason, or you write a correcting **Heuristic** in the **Local layer**. When the agent does **Recall**, the **Local layer** wins ties and the **Forget** closes the **Validity** window on the bad one."

> **Dev:** "And at session start, the agent only gets the **One-liners**, right?"

> **Domain expert:** "Right — **L0** (identity) plus **L1** (top-ranked **One-liners**), under 600 tokens total. Full **Content** is **L2** — pulled on demand via **Recall**."

> **Dev:** "Mid-task the agent spots a possible bug but lacks the context to fix it. Does it **Store** an **Observation**?"

> **Domain expert:** "No — it **Parks** a **Loose End**. That's open work, not knowledge, so the **Signal gate** doesn't apply and it can be terse and speculative. It stays **Open** and keeps surfacing in **Context** until resolved — it never **FadeMem**-decays the way an **Observation** would."

> **Dev:** "And once the bug is confirmed and understood?"

> **Domain expert:** "**Resolve** the **Loose End** as **Promoted** — that graduates it into an **Observation** or **Insight**, or links a GitHub issue. If it turns out to be a non-issue, **Resolve** it as **Dropped** — which is *not* **Forget**: **Forget** retires a **Memory**, **Resolve** closes a **Loose End**. Different concepts, different audit trails."

## Flagged ambiguities

- **"Memory" vs. "document"** — in RavenDB every **Memory** is stored as a document, but we reserve "document" for the storage primitive and always say **Memory** at the domain level.
- **"Layer" vs. "Repo"** — both scope **Memories**, but a **Repo** is the project namespace while a **Layer** is the read/write-and-sharing container. A single **Repo** can have multiple **Layers** (one **Local**, zero-or-more **Shared**/**Base**).
- **"Bundle" vs. "Pack"** — early prose used "bundle" generically; the canonical term is **Pack** (the markdown format). Reserve "bundle" only when talking about packaging as a generic concept.
- **"Search" vs. "Recall"** — both are used interchangeably in API paths, but **Recall** is the domain verb (ranked, typed, budgeted). Plain "search" is too generic and overlaps with full-text grep.
- **"Delete" vs. "Forget"** — Eidet has no hard delete. Say **Forget** to emphasize the append-only, audit-preserving semantics.
- **"Importance" vs. "Confidence"** — these are distinct: **Importance** is how much you want this **Memory** to rank, **Confidence** is how true you believe it to be. A critical-but-uncertain **Observation** is high **Importance**, low **Confidence**.
- **"Insight"** (domain) vs. **"insight"** (colloquial "aha moment") — the domain term means a *consolidated, confirmed* **Memory**, not a fresh realization. A fresh realization is an **Observation** until **Consolidation** promotes it.
- **"User"** is overloaded: in this domain prefer **Operator** for the human curator and **Agent** for the AI writer; reserve "user" only for end-user-preference **Memories**.
- **"Memory" vs. "Loose End"** — recalled knowledge vs. open work. The two were conflated in early discussion ("store a todo" / "park a future memory"); they are distinct concepts with distinct stores and verbs. A **Loose End** may **Promote** *into* a **Memory** but is not one.
- **"Resolve" vs. "Forget"/"TTL expiry"/"Supersession"** — all retire something, but **Resolve** closes a **Loose End** (open work) while the other three retire a **Memory** (knowledge). Keep **Resolved** a typed, first-class **Loose End** state; do not implement it by reusing a **Memory** closure path, or quality reports conflate a *done todo* with an *expired memory*.
- **"Todo"/"Task"** — informal aliases for **Loose End**; avoid as domain terms. They imply a work-tracker (assignees, due dates, priorities) Eidet deliberately is not.
- **"Done"** — overloaded: a **Loose End** **Resolution kind** (closed-as-handled) AND a **Signal gate** low-signal pattern (the bare word "done" is rejected as **Memory** **Content**). Same word, unrelated meanings.
- **"Park" vs. "Store"** — both persist, but **Park** creates a **Loose End** (terse, **Secret scanner** only) and **Store** creates a **Memory** (full **Write gate**). They are exposed as distinct MCP tools (`eidet_park`/`eidet_resolve` vs. `eidet_store`/`eidet_forget`).
- **"Promote" vs. "Supersession"** — both link entries, but **Promote** graduates a **Loose End** into a **Memory** (cross-concept) while **Supersession** replaces a **Memory** with a newer version of itself (same concept).
- **"Canon" vs. the memory store** — every **Memory** is knowledge, but **Canon** is only the curated, **Operator**-approved subset (**Domain**/**Term** pages). Don't call the whole store a knowledge base.
- **"Approve" vs. "Promote"** — parallel shape, distinct concepts: **Approve** graduates a **Canon draft** into a **Canon page**; **Promote** graduates a **Loose End** into a **Memory**. Keep the verbs separate.
- **"Domain"** (Canon) vs. "domain" (DDD-speak) — the **Canon** term means a tag-clustered page; lowercase "domain" in design conversation usually means the business domain. Qualify when ambiguous ("Canon Domain").
- **"Canon draft" vs. "Loose End"** — both are pending items outside `memories/*`, but a **Canon draft** is proposed *knowledge* awaiting review while a **Loose End** is open *work* awaiting action.
- **"Cue anchor" vs. "Entity" vs. "Tag"** — an **Entity** is the extracted *thing*; a **Cue anchor** is the *role* that **Entity** plays when **Expansion** uses it to reach a neighbouring **Memory**. Same data, different job — say **Cue anchor** only when talking about reachability. A **Tag** is neither: it is operator-facing grouping (and what **Consolidation** clusters on), and it is deliberately *not* an expansion path.
- **"Abstraction" vs. "One-liner"/"Summary"** — **One-liner** and **Summary** are stored **Enrichment** fields that may be absent; **Abstraction** is the *derived* value **Recall** actually embeds, which falls back through both to **Content** so it always exists. Say **Abstraction** when talking about the retrieval **Arm**, **One-liner** when talking about the stored field or the **L1** render.
