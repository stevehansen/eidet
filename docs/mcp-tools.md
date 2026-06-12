---
title: MCP Tools
nav_order: 5
---

# MCP Tools Reference

Eidet exposes 6 tools via the [Model Context Protocol](https://modelcontextprotocol.io/) — the core session-flow tools AI agents call directly. Advanced operations (intake, consolidation, maintenance, version history, curation, pack import/export) run server-side and are reached via the [REST API](#server-side-operations) and CLI, keeping the agent-facing surface small.

## Transports

| Transport | Command | Use case |
|-----------|---------|----------|
| **stdio** | `eidet mcp` | Local AI clients (Claude Code, Cursor) |
| **HTTP** | `POST http://localhost:19380/mcp` | Network MCP clients |

## Tool Listing

### eidet_store

Store a new memory.

```json
{
  "repo": "P:\\MyProject",
  "content": "The UserService caches results in Redis with a 5-minute TTL",
  "type": "observation",
  "tags": ["redis", "caching", "user-service"],
  "importance": 0.6,
  "source": "claude-session",
  "session_id": "sess-abc123",
  "supersedes": null
}
```

**Returns:** `{ "id": "memories/...", "success": true }` or rejection reason.

### eidet_recall

Recall memories by semantic query (hybrid vector + full-text search).

```json
{
  "repo": "P:\\MyProject",
  "query": "how does caching work",
  "limit": 10,
  "type": "insight"
}
```

**Returns:** Array of scored results with content, type, tags, importance, staleness warnings.

### eidet_context

Get the compact L0+L1 session context. This is typically the first tool an agent calls.

```json
{
  "repo": "P:\\MyProject"
}
```

**Returns:** A text block under 600 tokens with memory counts and top-scored memories.

On first call for a new repo (when no memories exist), automatically triggers intake to scan project files.

### eidet_forget

Soft-delete a memory with an optional reason.

```json
{
  "id": "memories/P--MyProject/observation/abc123",
  "reason": "This information is outdated"
}
```

Creates an audit trail observation recording what was forgotten and why.

### eidet_feedback

Report whether a recalled memory was useful.

```json
{
  "memory_id": "memories/P--MyProject/insight/abc123",
  "was_used": true
}
```

- `was_used: true` — **Echo**: boosts importance and confidence
- `was_used: false` — **Fizzle**: reduces importance and confidence

### eidet_link

Create a cross-repo link between two memories.

```json
{
  "source_id": "memories/P--ProjectA/insight/abc",
  "target_id": "memories/P--ProjectB/insight/def",
  "relation": "depends-on"
}
```

## Server-side operations

These are deliberately **not** on the MCP surface — agents rarely need them inline, and keeping them off the tool list reduces session overhead. They run automatically (scheduler/maintenance pipeline) or are invoked by an operator via the REST API, CLI, or Web UI:

| Operation | REST endpoint | Notes |
|-----------|---------------|-------|
| Intake | `POST /api/eidet/intake` | Also fires automatically on first `eidet_context` for a new repo |
| Consolidate | `POST /api/eidet/consolidate` | Also runs as a maintenance stage |
| Maintenance | `POST /api/maintenance` | TTL expiry, dedup, decay, enrichment, auto-consolidation |
| Version history | `GET /api/eidet/{id}` (version chain) | Supersession ancestry |
| Curation / edit | `PUT /api/eidet/{id}` | Versioned on content change |
| Pack export | `POST` export endpoints | Portable `.eidet` pack |
| Pack import | mount-as-layer endpoints | Import + mount a pack |

## Agent Integration Pattern

A typical AI agent session flow:

```
1. Session starts
2. Agent calls eidet_context → gets L0+L1 summary
3. Agent works on task, calls eidet_recall as needed
4. Agent discovers something → calls eidet_store
5. Agent uses a recalled memory → calls eidet_feedback(was_used: true)
6. Agent finds a memory wrong → calls eidet_forget with reason
7. Session ends (memories persist for next session)
```

## CLAUDE.md Instructions

Use `eidet instructions` to generate ready-made instructions for your CLAUDE.md:

```bash
# Print to stdout
eidet instructions --print

# Install into ~/.claude/CLAUDE.md
eidet instructions --install

# Create in project root
eidet instructions --project
```
