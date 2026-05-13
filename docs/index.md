---
title: Home
layout: home
nav_order: 1
---

# Eidet

**Long-term memory for AI coding agents — local-first, privacy-absolute, works everywhere.**

Eidet gives AI coding agents (Claude Code, Cursor, etc.) persistent, semantic memory across sessions. It runs as a local service on your machine, stores memories in RavenDB, and exposes them via MCP and REST APIs.

*From "eidetic" — relating to extraordinarily vivid, detailed recall.*

---

## Why Eidet?

AI coding agents start every session from scratch. They re-discover your architecture, re-learn your patterns, and repeat the same mistakes. Eidet fixes this by giving agents a structured memory that persists across sessions.

- **Observations** — things the agent notices ("The CI flakes when paths exceed 260 chars on Windows")
- **Insights** — confirmed knowledge distilled from observations ("Always use forward slashes in CI paths")
- **Procedures** — multi-step workflows ("To deploy: run tests, bump version, push tag, wait for CI")
- **Heuristics** — rules of thumb ("Prefer httpx over requests in this codebase")

## Key Features

| Feature | Description |
|---------|-------------|
| **Local-first** | Everything runs on your machine. No cloud, no telnet home. |
| **Semantic search** | Hybrid vector + full-text retrieval via RavenDB |
| **MCP + REST** | Works with any AI client that speaks MCP or HTTP |
| **4 memory types** | Typed memories with per-type budgets and decay rates |
| **Write gates** | Secret scanner + signal gate block low-value and sensitive content |
| **Auto-enrichment** | Optional Ollama integration for summaries, one-liners, foresight hints |
| **Lifecycle hooks** | Run custom scripts before/after store, recall, forget |
| **Web UI** | Built-in dashboard, memory browser, knowledge graph |
| **Client SDKs** | TypeScript, Python, C# — all with full type support |

## Quick Start

```bash
# Install (or download from GitHub releases)
dotnet tool install -g eidet

# First-time setup (detects RavenDB, configures embeddings)
eidet setup

# Register as system service + auto-configure every detected MCP client
# (Claude Code, Claude Desktop, Codex, Gemini)
eidet install

# Store a memory via CLI
eidet store "Always run migrations before seeding" --type heuristic
```

Verify with `eidet status` (shows service state and which MCP clients picked up eidet), then open [http://localhost:19380/ui](http://localhost:19380/ui) for the Web UI.

For one client at a time, use `eidet mcp install <client>`. To see registration status across clients, `eidet mcp list`.

## Architecture

```
  AI Agent (Claude Code, Cursor, etc.)
       │
       ├── MCP (stdio)  ──→  McpServer
       │                        │
       └── REST (HTTP)   ──→  EidetApiServer
                                │
                           MemoryService
                           ┌────┴────┐
                      WriteGates   HybridSearch
                      (Secret +    (Vector +
                       Signal)      Full-text)
                           │
                        RavenDB
                     (embedded or external)
```

## Next Steps

- [Getting Started]({% link getting-started.md %}) — installation and first memories
- [Concepts]({% link concepts.md %}) — how memory types, scoring, and retrieval work
- [REST API]({% link api-reference.md %}) — full endpoint reference
- [MCP Tools]({% link mcp-tools.md %}) — the 13-tool MCP surface
- [SDKs]({% link sdk/index.md %}) — TypeScript, Python, C# client libraries
