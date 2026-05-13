---
title: Getting Started
nav_order: 2
---

# Getting Started

## Prerequisites

- **.NET 10 SDK** — [Download](https://dotnet.microsoft.com/download)
- **RavenDB 7.x** — either:
  - Embedded mode (zero setup, Eidet manages it)
  - External instance at `http://localhost:8080` ([Download](https://ravendb.net/download))
- **Ollama** (optional) — for AI-powered enrichment ([Download](https://ollama.ai))

## Installation

### From GitHub Releases

Download the latest release for your platform from [GitHub Releases](https://github.com/stevehansen/eidet/releases):

| Platform | Asset |
|----------|-------|
| Windows x64 | `eidet-win-x64.zip` |
| macOS ARM | `eidet-osx-arm64.tar.gz` |
| Linux x64 | `eidet-linux-x64.tar.gz` |

Extract and add to your PATH.

### From Source

```bash
git clone https://github.com/stevehansen/eidet.git
cd eidet
dotnet build
dotnet run --project src/Eidet.Service
```

## First-Time Setup

Run the interactive setup wizard:

```bash
eidet setup
```

This will:
1. Detect or start RavenDB (embedded or external)
2. Create the `Eidet` database
3. Deploy search indexes (vector + full-text)
4. Configure `bge-micro-v2` embeddings
5. Optionally detect and configure Ollama

For non-interactive setup (CI/Docker):

```bash
eidet setup --non-interactive
```

### Embedded vs External RavenDB

| Mode | When to use | Setup |
|------|-------------|-------|
| **Embedded** | Zero-setup, single user | `eidet setup --embedded` |
| **External** | Already running RavenDB, need Studio access | Point to `http://localhost:8080` |

## Start the Service

```bash
eidet serve
```

Output:
```
Eidet v1.0.0
  RavenDB: Connected at http://localhost:8080
  API:     http://127.0.0.1:19380
  MCP:     http://127.0.0.1:19380/mcp
  Health:  http://127.0.0.1:19380/api/health
```

### Install as System Service

To run Eidet on startup and register with your AI clients in one shot:

```bash
eidet install
```

This installs as a Windows scheduled task / macOS launchd agent / Linux systemd user unit, then walks the MCP client registry and registers eidet with every detected client (Claude Code, Claude Desktop, Codex, Gemini). Already-configured clients are left alone — re-running is safe.

## Store Your First Memory

### Via CLI

```bash
eidet store "The auth module uses JWT with RS256 signing" --type observation --repo /path/to/project
eidet store "Always run db:migrate before db:seed" --type heuristic --tags database,setup
eidet store "Deploy: push to main, wait for CI, tag release" --type procedure --tags deployment
```

### Via REST API

```bash
curl -X POST http://localhost:19380/api/eidet \
  -H "Content-Type: application/json" \
  -d '{
    "repo": "/path/to/project",
    "content": "The auth module uses JWT with RS256 signing",
    "type": "observation",
    "tags": ["auth", "jwt"],
    "importance": 0.7
  }'
```

### Via MCP (from an AI agent)

The MCP server exposes `eidet_store` as a tool. AI agents call it automatically when they discover something worth remembering.

## Recall Memories

```bash
# Search by query
eidet recall "how does authentication work" --repo /path/to/project

# Get compact context (what agents see at session start)
curl "http://localhost:19380/api/eidet/context?repo=/path/to/project"
```

The context endpoint returns a compact summary (under 600 tokens) that agents load at session start — no manual configuration needed.

## Connect to AI Agents

The fastest path is `eidet install` — it registers eidet with every MCP-capable client it finds on your machine. To work one client at a time, use `eidet mcp install`:

```bash
eidet mcp install               # interactive picker over detected clients
eidet mcp install claude-code   # specific client
eidet mcp install --all         # everything detected
eidet mcp list                  # see current registration status
```

Supported clients:

| Client | How it's registered | Config file (fallback) |
|--------|---------------------|------------------------|
| `claude-code` | `claude mcp add --transport stdio -s user eidet -- eidet mcp` | `~/.claude.json` |
| `claude-desktop` | direct file write (no CLI) | `claude_desktop_config.json` (platform-specific) |
| `codex` | `codex mcp add eidet -- eidet mcp` | `~/.codex/config.toml` |
| `gemini` | `gemini mcp add eidet "eidet mcp" -s user` | — (CLI only) |

When the client's own CLI is on `PATH`, eidet rides on it so you get whatever schema upstream is using; otherwise it writes the config file directly.

### Any other MCP client

Eidet supports both stdio and HTTP MCP transports:

- **stdio**: point your client at the command `eidet mcp`
- **HTTP**: `POST http://localhost:19380/mcp`

### REST API

Any HTTP client can use the REST API at `http://localhost:19380/api/`. See the [API Reference]({% link api-reference.md %}) for all endpoints.

## Verify Everything Works

```bash
# Health check
eidet doctor

# Quick status
eidet status

# Open Web UI
# Navigate to http://localhost:19380/ui
```

## Next Steps

- [Concepts]({% link concepts.md %}) — understand memory types, scoring, and retrieval
- [Configuration]({% link configuration.md %}) — tune settings for your workflow
- [Hooks]({% link hooks.md %}) — run custom scripts on memory lifecycle events
