# Integration Spec: Eidet Client Integrations

> **Scope**: This spec defines WHO connects to the Eidet service and HOW. Each client integration is independent — the service is client-agnostic. Adding a new client means implementing one of two interfaces: MCP (for AI tools) or REST (for everything else).

---

## Integration Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         Clients                                      │
│                                                                     │
│  ┌─────────────┐ ┌───────────────┐ ┌──────────┐ ┌───────────────┐ │
│  │ Claude Code  │ │ Claude Desktop│ │ Cursor   │ │ Other AI CLI  │ │
│  │ (stdio MCP)  │ │ (stdio MCP)   │ │(stdio MCP│ │ (stdio MCP)   │ │
│  └──────┬──────┘ └───────┬───────┘ └────┬─────┘ └───────┬───────┘ │
│         │                │              │                │          │
│         └────────┬───────┴──────┬───────┴────────────────┘          │
│                  ▼              ▼                                    │
│         ┌──────────────────────────┐                                │
│         │ eidet mcp (stdio bridge)│                                │
│         └────────────┬─────────────┘                                │
│                      │ REST                                         │
│                      ▼                                              │
│  ┌───────────────────────────────────────────┐                     │
│  │          Memory Core Service               │                     │
│  │          localhost:19380                    │                     │
│  └───────────────────────────────────────────┘                     │
│                      ▲                                              │
│                      │ REST                                         │
│         ┌────────────┼────────────┐                                │
│         │            │            │                                  │
│  ┌──────┴──────┐ ┌──┴─────┐ ┌───┴──────┐ ┌─────────┐ ┌────────┐ │
│  │TerminalHost │ │ Web UI │ │ CLI      │ │ CI/CD   │ │ VS Code│ │
│  │ (REST)      │ │ (REST) │ │ (REST)   │ │ (REST)  │ │ (REST) │ │
│  └─────────────┘ └────────┘ └──────────┘ └─────────┘ └────────┘ │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Claude Code

The primary integration target. Claude Code supports MCP servers via stdio.

### Configuration

```json
// Option 1: Per-project (.mcp.json in project root)
{
  "mcpServers": {
    "eidet": {
      "command": "eidet",
      "args": ["mcp"],
      "env": {}
    }
  }
}

// Option 2: Global (~/.claude/claude_desktop_config.json)
{
  "mcpServers": {
    "eidet": {
      "command": "eidet",
      "args": ["mcp"],
      "env": {}
    }
  }
}
```

The `eidet install` command auto-configures this if Claude Code is detected — it writes the `mcpServers.eidet` entry to `~/.claude/claude_desktop_config.json`. Idempotent: skips if already configured.

### Session Lifecycle

```
1. Claude Code starts session
   → Launches `eidet mcp` subprocess
   → MCP initialize with workspaceFolders

2. eidet mcp bridge:
   → Extracts working directory from initialize params
   → Connects to local Eidet REST API
   → Returns tool list (13 memory_* tools)

3. Claude Code auto-hook (PostToolUse: initialize):
   → Calls eidet_context → L0+L1 injected into conversation
   → ~600 tokens of persistent project knowledge
   → If no memories exist, auto-intake runs (CLAUDE.md, README, etc.)

4. During session:
   → Agent calls eidet_store, eidet_recall, eidet_feedback, etc.
   → Bridge proxies to local service via REST
   → Service handles all logic (scoring, gating, search, enrichment)

5. Session end:
   → Bridge process exits
   → Service runs auto-consolidation for observations from this session
```

### CLAUDE.md Instructions

The Eidet service can install usage instructions into `~/.claude/CLAUDE.md`:

```bash
eidet instructions install
# Appends memory usage guidelines to global CLAUDE.md
# Includes: when to store, recall patterns, feedback guidelines, tag conventions
```

This ensures Claude Code agents know how to use the memory system effectively from the first session.

---

## Claude Desktop

Same MCP stdio integration as Claude Code.

```json
// ~/Library/Application Support/Claude/claude_desktop_config.json (macOS)
// %APPDATA%\Claude\claude_desktop_config.json (Windows)
{
  "mcpServers": {
    "eidet": {
      "command": "eidet",
      "args": ["mcp"]
    }
  }
}
```

Difference from Claude Code: Claude Desktop doesn't have a working directory concept. The bridge can:
1. Use a default "general" repo namespace for non-project conversations
2. Accept a `--repo` argument for explicit namespace: `"args": ["mcp", "--repo", "general"]`
3. Detect project context from conversation content (if the user mentions a project path)

---

## Cursor / Windsurf / Cline

Any MCP-compatible editor extension. Same stdio pattern:

```json
{
  "mcpServers": {
    "eidet": {
      "command": "eidet",
      "args": ["mcp"]
    }
  }
}
```

The MCP bridge extracts working directory from the `initialize` params that these tools send. Each tool sends the current workspace folder, which maps to a repoId.

---

## TerminalHost

TerminalHost is a rich client that talks directly to the service REST API.

### Detection & Connection

```csharp
public class MemoryServiceClient
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:19380") };

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/health");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
```

On startup:
1. Check if Eidet service is running (`GET /api/health`)
2. If available: use service API for all memory operations
3. If not available: show banner "Install Eidet for persistent AI memory"
4. Settings panel allows configuring service URL (for non-default port)

### Refactored MemoryHostService

```csharp
// Before (embedded):
public class MemoryHostService
{
    private RavenMemoryStore? _store;
    private MemoryService? _memoryService;
    // ... creates and manages RavenDB, indexes, all services directly
}

// After (service client):
public class MemoryHostService
{
    private readonly MemoryServiceClient _client;
    
    public async Task<MemoryContextResult> GetContextAsync(string repoId)
        => await _client.GetAsync<MemoryContextResult>($"/api/eidet/context?repo={repoId}");
    
    public async Task<List<MemorySearchResult>> RecallAsync(string repoId, string query)
        => await _client.GetAsync<List<MemorySearchResult>>($"/api/eidet/search?repo={repoId}&q={query}");
    
    // ... thin REST wrapper for all operations
}
```

### UI Panels (Unchanged UX)

| Panel | Data Source (before) | Data Source (after) |
|-------|---------------------|---------------------|
| Memory Browser | Direct RavenDB queries | `GET /api/eidet/search` |
| Settings | Local AppSettings.Memory | `GET/PUT /api/config` |
| Debug Log | Local service logging | `GET /api/logs` (SSE stream) |
| Layer Stack | LayerService directly | `GET /api/eidet/layers` |

Users see no difference. Same panels, same interactions, backed by the service.

### Migration

For users with existing embedded memories:

1. TerminalHost Settings → Memory → "Migrate to Eidet" button
2. Exports all memories via `memory_bundle_export` format
3. Calls `POST /api/eidet/bundles/import` on the service
4. Reconfigures TerminalHost to use service API
5. Shows migration summary: "Migrated 47 memories across 3 repos"
6. Old embedded RavenDB data preserved as backup

---

## Web UI

A single-page application served by the remote backend (SaaS or self-hosted) or locally by the service.

### Features

| Feature | Description |
|---------|-------------|
| **Knowledge Graph** | Obsidian-like force-directed graph of memories and their links (supports, conflicts, refines). Cross-repo connections visible. |
| **Memory Browser** | Search, filter by type/tags/repo, detail panel with entities, foresight, confidence, provenance. Markdown rendering. |
| **Timeline View** | Chronological view of memory evolution. Validity intervals as bars. Consolidation events highlighted. |
| **Team Dashboard** | Team layer browser, member list, publishing activity, shared knowledge stats. |
| **Settings** | Service configuration, sync status, team management, bundle catalog. |
| **Import/Export** | Upload .mempack files, export memories as markdown, bulk operations. |

### Local Web UI

The Eidet service can serve a lightweight web UI locally:

```
http://localhost:19380/ui
```

This provides a browser-based memory viewer without needing the remote backend. Useful for users who want a UI without TerminalHost.

### Remote Web UI

```
https://app.eidet.dev
# or: https://memory.internal.company.com (self-hosted)
```

Full SPA with auth, team features, graph visualization. For personal memories (E2E encrypted), decryption happens in the browser using the user's key (stored in browser crypto storage or prompted).

---

## CI/CD Pipelines

Store build insights, test results, deployment notes as memories via REST API.

### GitHub Actions Example

```yaml
- name: Store build insight
  if: failure()
  run: |
    curl -X POST http://localhost:19380/api/eidet \
      -H "Content-Type: application/json" \
      -d '{
        "repoId": "${{ github.repository }}",
        "type": "observation",
        "content": "Build failed: ${{ steps.build.outputs.error }}",
        "tags": ["ci", "build-failure"],
        "importance": 0.6,
        "provenance": "ToolOutput"
      }'
```

### Use Cases

- Store recurring build failure patterns as heuristics
- Record deployment procedures as they evolve
- Track which tests are flaky (observation → insight after consolidation)
- Store performance regression observations

**Note**: CI/CD environments may not have the Eidet service running locally. Options:
1. Install Eidet service in CI (lightweight, works with embedded RavenDB)
2. Call remote API directly (with API key auth)
3. Use a CI-specific memory bridge that batches observations and syncs later

---

## VS Code Extension (Future)

Potential VS Code extension that integrates memory into the editor.

### Features (Conceptual)

- **Memory sidebar**: Browse/search memories for current workspace
- **Inline annotations**: Show relevant memories as CodeLens on files
- **Quick store**: Select code → "Store as memory" command
- **Recall on hover**: Hover over a function → show related memories
- **Git integration**: On commit, suggest observations to store

### Implementation

REST API calls to the local Eidet service. Same API surface as TerminalHost.

---

## Custom Integrations

Any tool can integrate via the REST API:

```bash
# Store a memory
curl -X POST http://localhost:19380/api/eidet \
  -H "Content-Type: application/json" \
  -d '{"repoId": "my-repo", "type": "insight", "content": "...", "tags": ["api"]}'

# Recall memories
curl "http://localhost:19380/api/eidet/search?repo=my-repo&q=authentication&limit=5"

# Get context (for injection into any AI prompt)
curl "http://localhost:19380/api/eidet/context?repo=my-repo"
```

The context endpoint is particularly useful: any tool that constructs AI prompts can include memory context by making a single GET request.

---

## DevContainer Support

The Eidet service runs on the host machine. DevContainers need to reach it.

### Option 1: Host Network Access

Docker containers with `--network=host` or port forwarding:

```dockerfile
# devcontainer.json
{
  "forwardPorts": [19380],
  "remoteEnv": {
    "EIDET_API_URL": "http://host.docker.internal:19380"
  }
}
```

### Option 2: Memory MCP Bridge Inside Container

Install the `eidet` CLI inside the container. It connects to the host service:

```dockerfile
# In Dockerfile
RUN curl -fsSL https://get.eidet.dev | bash
```

The bridge detects it's in a container and uses `host.docker.internal` or the forwarded port.

### Option 3: Remote API

If the Eidet service has remote sync enabled, the devcontainer can connect to the remote backend directly:

```json
{
  "remoteEnv": {
    "EIDET_API_URL": "https://api.eidet.dev",
    "EIDET_API_KEY": "..."
  }
}
```

---

## Multi-Device Usage

The same user on multiple machines:

```
Laptop (local service + sync)          Desktop (local service + sync)
  │                                      │
  │  ┌──────────────────────┐           │
  └──│  Remote Backend      │───────────┘
     │  (syncs all events)  │
     └──────────────────────┘
```

- Both devices have complete local copies
- Events sync in real-time when both are online
- Offline device catches up when reconnected
- Same memories, same layers, same context on both machines
- Different repos on different machines → different L0/L1 context, but shared cross-repo links

---

## API Key Authentication (for Programmatic Access)

For CI/CD and custom integrations that need to call the remote API:

```bash
# Generate API key
eidet api-key create --name "GitHub Actions" --scopes "write:observations,read:all"

# Use in requests
curl -H "Authorization: Bearer eidet_abc123..." https://api.eidet.dev/api/eidet
```

API keys are scoped:
- `read:all` — read any memory
- `write:observations` — store observations only (CI/CD use case)
- `write:all` — store any type
- `admin` — team management, maintenance, config

---

## Client SDK (Future)

For easier programmatic integration:

```typescript
// TypeScript/JavaScript
import { EidetClient } from '@eidet/sdk';

const memory = new EidetClient({ url: 'http://localhost:19380' });

await memory.store({
  type: 'insight',
  content: 'This API uses pagination with cursor tokens',
  tags: ['api', 'pagination'],
  importance: 0.7,
});

const results = await memory.recall('how does pagination work?');
```

```csharp
// C# / .NET
var memory = new EidetClient("http://localhost:19380");

await memory.StoreAsync(new StoreRequest
{
    Type = MemoryType.Insight,
    Content = "This API uses pagination with cursor tokens",
    Tags = ["api", "pagination"],
});

var results = await memory.RecallAsync("how does pagination work?");
```

```python
# Python
from eidet_sdk import EidetClient

memory = EidetClient("http://localhost:19380")

memory.store(
    type="insight",
    content="This API uses pagination with cursor tokens",
    tags=["api", "pagination"],
)

results = memory.recall("how does pagination work?")
```
