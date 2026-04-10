---
title: MCP Tools
nav_order: 5
---

# MCP Tools Reference

Eidet exposes 13 tools via the [Model Context Protocol](https://modelcontextprotocol.io/). These are the tools AI agents call directly.

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

Search memories by semantic query.

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

### eidet_history

View the version chain for a memory (current + all ancestors via supersession).

```json
{
  "id": "memories/P--MyProject/insight/abc123"
}
```

### eidet_intake

Ingest project files (CLAUDE.md, README.md, .editorconfig, package config) as seed memories. Splits by headings, deduplicates by content hash, extracts entities.

```json
{
  "repo": "P:\\MyProject"
}
```

### eidet_link

Create a cross-repo link between two memories.

```json
{
  "source_id": "memories/P--ProjectA/insight/abc",
  "target_id": "memories/P--ProjectB/insight/def",
  "relation": "depends-on"
}
```

### eidet_consolidate

Group related observations and create insights. Runs FadeMem decay on all memories.

```json
{
  "repo": "P:\\MyProject"
}
```

### eidet_maintenance

Run the full 7-stage maintenance pipeline:

1. TTL expiry
2. Observation retention (default 90 days)
3. Deduplication sweep (Jaccard similarity 0.85)
4. Importance decay
5. Orphan cleanup
6. Backfill enrichment (entities + one-liners)
7. Auto-consolidation

```json
{
  "repo": "P:\\MyProject"
}
```

### eidet_export

Export all memories as formatted markdown.

```json
{
  "repo": "P:\\MyProject"
}
```

### eidet_pack_export

Export memories as a portable `.eidet` pack file for sharing.

```json
{
  "repo": "P:\\MyProject"
}
```

### eidet_pack_import

Import a `.eidet` pack and optionally mount it as a layer.

```json
{
  "repo": "P:\\MyProject",
  "data": "...",
  "mount_as_layer": true
}
```

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
