---
title: REST API
nav_order: 4
---

# REST API Reference

Base URL: `http://localhost:19380`

All endpoints return JSON. Requests with a body expect `Content-Type: application/json`.

## Authentication

When API key auth is enabled, include the key as a Bearer token:

```
Authorization: Bearer eidet_abc123...
```

Health, status, and Web UI endpoints are always public.

---

## Health & Status

### GET /api/health

Quick health check.

```bash
curl http://localhost:19380/api/health
```

```json
{
  "status": "healthy",
  "version": "1.0.0"
}
```

### GET /api/status

Detailed service status.

```bash
curl http://localhost:19380/api/status
```

```json
{
  "version": "1.0.0",
  "status": "running",
  "uptime": "2d 5h 30m",
  "api": "http://127.0.0.1:19380"
}
```

---

## Core Operations

### POST /api/eidet — Store a Memory

```bash
curl -X POST http://localhost:19380/api/eidet \
  -H "Content-Type: application/json" \
  -d '{
    "repo": "P:\\MyProject",
    "content": "The auth module uses JWT with RS256 signing",
    "type": "observation",
    "tags": ["auth", "jwt"],
    "importance": 0.7,
    "source": "user",
    "sessionId": "session-abc",
    "supersedes": null
  }'
```

**Response (stored):**
```json
{ "id": "memories/P--MyProject/observation/a1b2c3d4e5f6", "success": true }
```

**Response (duplicate):**
```json
{ "duplicateId": "memories/P--MyProject/observation/existing123", "success": false }
```

**Response (rejected):**
```json
{ "reason": "Content blocked by secret scanner: AWS access key detected", "success": false }
```

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `repo` | string | yes | — | Repository/project identifier |
| `content` | string | yes | — | The memory text |
| `type` | string | yes | — | `observation`, `insight`, `procedure`, or `heuristic` |
| `tags` | string[] | no | `[]` | Searchable tags |
| `importance` | float | no | `0.5` | 0.0–1.0 importance score |
| `source` | string | no | `"claude-session"` | Origin identifier |
| `sessionId` | string | no | `null` | Session tracking |
| `supersedes` | string | no | `null` | ID of memory this replaces |

### GET /api/eidet/recall — Recall Memories

_Legacy alias: `GET /api/eidet/search` (same response)._

```bash
curl "http://localhost:19380/api/eidet/recall?repo=P%3A%5CMyProject&q=authentication&limit=5&type=insight"
```

```json
{
  "results": [
    {
      "id": "memories/P--MyProject/insight/abc123",
      "repoId": "P--MyProject",
      "type": "Insight",
      "content": "The auth module uses JWT with RS256...",
      "oneLiner": "JWT RS256 auth in auth module",
      "tags": ["auth", "jwt"],
      "importance": 0.7,
      "score": 0.95,
      "createdAt": "2026-03-15T10:30:00Z",
      "ageDays": 26,
      "stalenessWarning": null
    }
  ]
}
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `repo` | string | required | Repository identifier |
| `q` | string | required | Search query |
| `limit` | int | `10` | Max results |
| `type` | string | — | Filter by memory type |

### GET /api/eidet/context — Get Session Context

Returns the compact L0+L1 context (under 600 tokens) that agents load at session start.

```bash
curl "http://localhost:19380/api/eidet/context?repo=P%3A%5CMyProject"
```

```json
{
  "context": "[Memory: 42 entries, 15 observations, 18 insights, 5 procedures, 4 heuristics]\n[I] JWT RS256 auth in auth module\n[P] Deploy: push main → CI → tag → verify\n[H] Always use forward slashes in CI paths\n..."
}
```

### GET /api/eidet/{id} — Get a Memory

```bash
curl "http://localhost:19380/api/eidet/memories%2FP--MyProject%2Finsight%2Fabc123"
```

Returns the full `MemoryEntry` object with all fields.

### DELETE /api/eidet/{id} — Forget a Memory

```bash
curl -X DELETE "http://localhost:19380/api/eidet/memories%2FP--MyProject%2Finsight%2Fabc123?reason=outdated"
```

```json
{ "forgotten": true }
```

### POST /api/eidet/feedback — Echo/Fizzle

Report whether a recalled memory was useful.

```bash
curl -X POST http://localhost:19380/api/eidet/feedback \
  -H "Content-Type: application/json" \
  -d '{"memoryId": "memories/P--MyProject/insight/abc123", "wasUsed": true}'
```

```json
{ "applied": true }
```

### GET /api/eidet/history/{id} — Version Chain

```bash
curl "http://localhost:19380/api/eidet/history/memories%2FP--MyProject%2Finsight%2Fabc123"
```

```json
{
  "chain": [
    { "id": "memories/P--MyProject/insight/abc123", "content": "...", "createdAt": "2026-04-01T..." },
    { "id": "memories/P--MyProject/observation/def456", "content": "...", "createdAt": "2026-03-15T..." }
  ]
}
```

---

## Browse & Graph

### GET /api/eidet/browse — Paginated Browse

Browse memories without a search query.

```bash
curl "http://localhost:19380/api/eidet/browse?repo=P%3A%5CMyProject&skip=0&take=20&type=insight"
```

```json
{
  "repo": "P--MyProject",
  "skip": 0,
  "take": 20,
  "count": 15,
  "entries": [ ... ]
}
```

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `repo` | string | required | Repository identifier |
| `skip` | int | `0` | Pagination offset |
| `take` | int | `50` | Page size |
| `type` | string | — | Filter by memory type |

### GET /api/eidet/repos — List Repositories

```bash
curl http://localhost:19380/api/eidet/repos
```

```json
{
  "repos": [
    { "repoId": "P--MyProject" },
    { "repoId": "P--OtherProject" }
  ]
}
```

### GET /api/eidet/graph — Knowledge Graph

Returns nodes and edges for visualization.

```bash
curl "http://localhost:19380/api/eidet/graph?repo=P%3A%5CMyProject&limit=200"
```

```json
{
  "nodes": [
    { "id": "memories/...", "type": "Insight", "label": "JWT RS256 auth", "importance": 0.8, "tags": ["auth"] }
  ],
  "edges": [
    { "from": "memories/...", "to": "memories/...", "relation": "derived" }
  ]
}
```

---

## Operations

### POST /api/eidet/intake — Ingest Project Files

Scans CLAUDE.md, README.md, .editorconfig, package.json, etc. and creates seed memories.

```bash
curl -X POST "http://localhost:19380/api/eidet/intake?repo=P%3A%5CMyProject"
```

```json
{ "newCount": 12, "skippedCount": 3 }
```

### POST /api/eidet/consolidate — Run Consolidation

Groups related observations into insights.

```bash
curl -X POST "http://localhost:19380/api/eidet/consolidate?repo=P%3A%5CMyProject"
```

```json
{ "candidates": 45, "insightsCreated": 3, "insightsBoosted": 2 }
```

### POST /api/maintenance — Run Maintenance

Runs the full 7-stage maintenance pipeline (TTL expiry, retention, dedup, decay, cleanup, enrichment, consolidation).

```bash
curl -X POST "http://localhost:19380/api/maintenance?repo=P%3A%5CMyProject"
```

### GET /api/eidet/export — Export as Markdown

```bash
curl "http://localhost:19380/api/eidet/export?repo=P%3A%5CMyProject"
```

Returns plain markdown text with all memories organized by type.

---

## Layers

### GET /api/eidet/layers — List Mounted Layers

```bash
curl "http://localhost:19380/api/eidet/layers?repo=P%3A%5CMyProject"
```

### POST /api/eidet/layers — Mount a Layer

```bash
curl -X POST http://localhost:19380/api/eidet/layers \
  -H "Content-Type: application/json" \
  -d '{"repo": "P:\\MyProject", "layerId": "shared-knowledge", "type": "shared"}'
```

### DELETE /api/eidet/layers/{layerId} — Unmount a Layer

```bash
curl -X DELETE "http://localhost:19380/api/eidet/layers/shared-knowledge?repo=P%3A%5CMyProject"
```

---

## Packs (Import/Export)

### POST /api/eidet/packs/export — Export as .eidet Pack

```bash
curl -X POST "http://localhost:19380/api/eidet/packs/export?repo=P%3A%5CMyProject" -o project.eidet
```

### POST /api/eidet/packs/import — Import a .eidet Pack

```bash
curl -X POST http://localhost:19380/api/eidet/packs/import \
  -H "Content-Type: application/json" \
  -d '{"repo": "P:\\MyProject", "data": "..."}'
```

---

## MCP over HTTP

### POST /mcp — JSON-RPC over HTTP

For MCP clients that use HTTP transport instead of stdio.

```bash
curl -X POST http://localhost:19380/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc": "2.0", "method": "tools/list", "id": 1}'
```

---

## Web UI

Open `http://localhost:19380/ui` in your browser. The Web UI provides:

- **Dashboard** — repo selector, memory counts, recent memories
- **Browser** — search and browse with type filters and pagination
- **Knowledge Graph** — interactive force-directed graph visualization
- **Timeline** — chronological view grouped by date
- **Settings** — service status and action buttons
