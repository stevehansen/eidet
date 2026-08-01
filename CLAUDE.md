# CLAUDE.md — Eidet

> Long-term memory for AI coding agents — local-first, privacy-absolute, works everywhere.
> *From "eidetic" — extraordinarily vivid, detailed recall.*

A standalone local service that gives AI coding agents persistent, semantic memory across sessions.

## Domain Documentation (Living Specs)

Above the flat `UBIQUITOUS_LANGUAGE.md` glossary, each business domain has a **living spec** paired with
a **priming skill**, indexed in [docs/domains/README.md](docs/domains/README.md):

- **Living spec** `docs/domains/<domain>.md` — deep, human-facing current-state doc: entities,
  invariants, key files, gotchas, and the tests that pin each invariant down.
- **Priming skill** `.claude/skills/<domain>/SKILL.md` — thin, agent-facing; loads the essentials fast
  and links *down* to the spec.

**Start from the domain index.** It lists all twelve domains — memory, writepath, recall, maintenance,
enrichment, intake, looseends, canon, memorytool, sharing, portal, quality — links both artifacts for
each, names what is deliberately *not* a domain, and documents the template for adding one.

**Same-PR sync rule:** any change to a domain's behavior updates its living spec **in the same PR** as
the code change — never as a follow-up. If the change alters a load-bearing invariant, update the
priming skill too. A domain-behavior diff with no matching spec edit is incomplete. (Same discipline as
the `STRIDE.md` and `UBIQUITOUS_LANGUAGE.md` rules below.)

Auditing and adding domains is handled by the user-level `domain-priming` skill.

## Ubiquitous Language

`UBIQUITOUS_LANGUAGE.md` (repo root) is the canonical domain glossary — the agreed vocabulary for
memories, types, layers, retrieval, the write path, lifecycle, Loose Ends, memory-tool files, Canon, and
sharing. Use these terms in code, comments, and UI; consult its "Flagged ambiguities" before naming a
new concept — notably **Memory vs Loose End** (knowledge vs open work), **Resolve vs Forget / TTL expiry
/ Supersession** (closing open work vs retiring a memory), **Park vs Store**, and **Approve vs Promote**.
Update it in the same PR when introducing or renaming a domain concept.

## Non-negotiables

These hold across every domain; the per-domain invariants live in the living specs.

- **Zero-LLM write path** — stores are deterministic; a model is only ever involved in optional
  background enrichment. No write path may take an LLM dependency.
- **Secret scanning is always-on** — it runs before any storage, on every write surface, and cannot be
  disabled. Surfaces may skip the *semantic* gates (low-signal, self-talk); never this one.
- **Append-only** — there is no hard delete. `Forget` closes a validity interval and records a reason;
  content edits supersede rather than mutate.
- **Writes always land in the Local layer** — Shared and Base layers are read-only.
- **Single-repo by default** — `CrossRepo` defaults to `false`; opting in is explicit.
- **Never expose the service off localhost without auth** — the network-binding guard enforces it; API
  keys are SHA256-hashed and scoped (`/ui` and health are the only exemptions).
- **Under 600 tokens at wake-up** — L0 identity plus dense-packed L1 one-liners.
- **Generated files are never hand-edited** — `docs/benchmark.md` and `docs/swe-context-bench.md` are
  asserted byte-equal in CI; regenerate them.

## Specs

Design intent and rationale live in `docs/specs/`. Living specs (above) describe what is true *today*
and link down into these.

| Spec | Covers |
|------|--------|
| [CoreSpec.md](docs/specs/CoreSpec.md) | Domain model, 4 memory types, layers, tiered loading (L0/L1/L2), scoring, hybrid retrieval, write gates, consolidation, FadeMem decay, maintenance pipeline |
| [ServiceSpec.md](docs/specs/ServiceSpec.md) | Daemon, MCP server, REST API, RavenDB (embedded/external), TUI, Docker, installation, CLI, dependency philosophy |
| [IntegrationSpec.md](docs/specs/IntegrationSpec.md) | Claude Code, Claude Desktop, Cursor, TerminalHost, Docker, CI/CD, client SDKs |
| [SyncSpec.md](docs/specs/SyncSpec.md) | *(Future)* Remote sync, E2E encryption, team sharing |

- Architecture deep dive: [docs/deep-dive.md](docs/deep-dive.md)
- Phase-by-phase implementation history: [docs/phases.md](docs/phases.md)

## Tech Stack

- **.NET 10** (latest SDK)
- **RavenDB 7.x** — hybrid search + built-in embeddings (bge-micro-v2); embedded for zero-setup,
  external for existing installs
- **Spectre.Console** — TUI + CLI
- **HttpListener** — REST API, no ASP.NET dependency (lightweight, matches TerminalHost)

Minimal dependencies — adding one needs a reason. See ServiceSpec.md "Dependency Philosophy".

## Project Structure

```
eidet/
├── src/
│   ├── Eidet.Core/              # Domain, gates, indexes, services, storage
│   ├── Eidet.Service/           # CLI, REST API, MCP, scheduler, embedded Web UI
│   └── Eidet.Sync/              # Sync adapters (future)
├── tools/Eidet.Bench/           # SWE Context Bench harness (outside src/ — easy to miss)
├── sdk/{typescript,python,dotnet}/
├── tests/{Core,Service,Integration,Bench,Benchmark}.Tests/
└── docs/{domains/,specs/,phases.md,deep-dive.md}   # domains/ + specs/ are excluded from eidet.dev
```

## Interfaces

MCP (stdio + HTTP), REST, CLI, Web UI (`/ui`), and SDKs (TS/Python/C#) all sit on one set of tool
handlers dispatched by `ToolDispatcher`; MCP exposure is gated per handler by `IToolHandler.McpExposed`.
The agent-facing MCP surface is deliberately slim — session-flow tools only (context, recall, store,
feedback, forget, link, park, resolve). Everything else (history, intake, consolidate, maintenance,
edit, redact, pack import/export, Canon review) is REST/CLI/Web-UI only.

REST base: `http://localhost:19380`. Route rules that are easy to get wrong:

- **`ApiRouter.cs` is the authority on routes**, not a table — a new `GET` route must be registered
  *before* the catch-all or it will never match.
- `repo` is always the filesystem path; memory ids are `memories/{normalized-repo}/{type}/{hash}` and
  must be URL-encoded in a path segment.
- Published endpoint documentation: [docs/api-reference.md](docs/api-reference.md) (user-facing) —
  update it when adding a route.

## Installation

Shipped as a dotnet tool (primary) + standalone binaries for Docker/non-.NET.

```bash
dotnet tool install -g eidet
eidet setup      # interactive wizard (RavenDB, Ollama, embeddings)
eidet install    # registers service + auto-configures Claude Code/Desktop MCP
eidet update     # stop service → update → restart
```

Distribution: NuGet (`eidet`, `Eidet.Sdk`), npm (`@eidet/sdk`), PyPI (`eidet-sdk`), Docker
(`eidet/eidet:latest`), GitHub Releases.

MCP config (written by `eidet install`):
- Claude Code: `~/.claude.json` → `mcpServers.eidet`
- Claude Desktop: `%APPDATA%\Claude\claude_desktop_config.json` / `~/Library/Application Support/Claude/claude_desktop_config.json`

## Security — STRIDE.md

`STRIDE.md` is this repo's threat model. **You owe it an update, in the same PR, whenever a change:**

- adds or alters authentication/authorization, data storage, encryption, or secrets handling;
- adds an external integration, API endpoint, or MCP tool;
- moves a trust boundary; or
- **mitigates or resolves an existing finding** — move it to Mitigated/Resolved and update the
  mitigation text, score/status, and risk-summary row.

Updates are bidirectional: introducing *and* closing a threat both ship with the code. A
security-relevant diff with no `STRIDE.md` change is incomplete, and a fix is not done until the
document and the linked issue reflect it. Findings scoring ≥ 8 need a GitHub issue labelled `security`.

The scoring method, category taxonomy, ASVS citation format, and review-history convention live in the
document itself and in the `stride` skill — use the skill rather than restating the format here.

## Links

- **GitHub**: https://github.com/stevehansen/eidet (private)
- **Domain**: eidet.dev
