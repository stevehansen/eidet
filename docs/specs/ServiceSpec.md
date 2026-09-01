# Service Spec: Eidet Local Service

> **Scope**: This spec defines HOW Eidet runs — the local system service, MCP/REST hosting, storage options, installation, interactive TUI, Docker integration, updates, configuration, and the relationship to host applications like TerminalHost. The local service is the product. Remote sync (covered in [SyncSpec](SyncSpec.md)) is a future addition.

---

## Design Principles

1. **The local service is the product** — fully functional offline, always available, zero cloud dependency.
2. **Simplicity for the user, complexity for the app** — `eidet setup` walks you through everything. No confusing questions or errors.
3. **Always running** — system service (daemon) that starts on boot, auto-restarts on crash.
4. **Deduct and suggest** — detect existing RavenDB/Ollama instances, suggest defaults, but always allow full customization.
5. **Built for both humans and AI agents** — rich TUI for developers, structured JSON output for AI-driven setup.
6. **Security and privacy are absolute** — localhost-only, secret scanning, no data leaves the machine.
7. **Minimal dependencies** — only what's necessary. Microsoft ecosystem is fine (AI abstractions, JSON, HTTP). No unnecessary third-party libraries.
8. **Docker-native** — first-class support for devcontainers and custom Docker setups, with built-in troubleshooting.

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│  System Service (eidet / eidetd)                              │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐   │
│  │  MCP Server                                          │   │
│  │  • stdio transport (launched by AI clients)           │   │
│  │  • Streamable HTTP transport (for network clients)    │   │
│  │  • 13 eidet tools + tool listing                      │   │
│  └──────────────────────────────────────────────────────┘   │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐   │
│  │  REST API (localhost:19380)                           │   │
│  │  • /api/eidet/* endpoints (memory operations)         │   │
│  │  • /api/health (service discovery)                    │   │
│  │  • /api/config (read/write settings)                  │   │
│  └──────────────────────────────────────────────────────┘   │
│                                                              │
│  ┌────────────────┐  ┌────────────────┐                     │
│  │ Eidet.Core     │  │ Scheduler      │                     │
│  │ (domain logic) │  │ (maintenance,  │                     │
│  │                │  │  consolidation,│                     │
│  │                │  │  enrichment)   │                     │
│  └────────┬───────┘  └────────────────┘                     │
│           │                                                  │
│  ┌────────┴──────────────────────────────────────────────┐  │
│  │  RavenDB (embedded OR external)                       │  │
│  │  Database: Eidet  | Index: Memories/Search            │  │
│  │  Built-in embeddings (bge-micro-v2)                   │  │
│  └───────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────┘
```

---

## Storage: RavenDB Embedded vs External

Most developer machines that will use Eidet are also development systems — many already run RavenDB for their projects. Running RavenDB Embedded inside the service means a second RavenDB process, which wastes resources.

### Two Modes

| Mode | When to use | How |
|------|-------------|-----|
| **External** (recommended for devs) | RavenDB already running (e.g., for development) | Connect to existing instance, create `Eidet` database |
| **Embedded** (zero-setup) | No RavenDB installed, or want full isolation | Service bundles RavenDB Embedded, manages its own data |

### Auto-Detection During Setup

```
eidet setup

  Checking for RavenDB...
  ✓ Found RavenDB at http://localhost:8080 (v7.2.1)
    Database "Eidet" does not exist yet.

  ? Use this RavenDB instance? (recommended) [Y/n]
  → Creating database "Eidet"...
  → Deploying indexes...
  → Configuring embeddings (bge-micro-v2)...
  ✓ RavenDB configured

  Alternative: eidet setup --embedded  (bundles its own RavenDB)
```

If no RavenDB is found:
```
  Checking for RavenDB...
  ✗ No RavenDB instance found

  Options:
  1. Install RavenDB (recommended — also useful for development)
     → Opens: https://ravendb.net/download
  2. Use embedded RavenDB (zero-setup, ~80MB extra memory)

  ? Choose [1/2]:
```

### Configuration

```json
{
  "storage": {
    "mode": "external",
    "ravenUrl": "http://localhost:8080",
    "databaseName": "Eidet"
  }
}
```

Or for embedded:
```json
{
  "storage": {
    "mode": "embedded",
    "dataDir": "~/.eidet/data/raven",
    "databaseName": "Eidet"
  }
}
```

### Switching Modes

```bash
eidet config set storage.mode external
eidet config set storage.ravenUrl "http://localhost:8080"
eidet doctor  # Tests the new connection
```

Data migration between modes is automatic — the database schema is identical.

---

## MCP Server

### stdio Transport

AI clients launch the binary with `mcp` subcommand:

```json
// Claude Code: .mcp.json or global config
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

The `eidet mcp` subcommand:
1. Deploys indexes (idempotent — ensures schema changes take effect)
2. Connects directly to RavenDB via MemoryService (no HTTP hop)
3. Processes JSON-RPC over stdio
4. Auto-intake on first `eidet_context` call if no memories exist for this repo

### Streamable HTTP Transport

For network MCP clients, the service exposes MCP over HTTP at `/mcp`:
```
POST http://localhost:19380/mcp
Content-Type: application/json
```

### Session Identity

Working directory resolution order:
1. MCP `initialize` params (`roots` or `workspaceFolders`)
2. `--workdir` argument
3. Current working directory of the launching process

Normalized to a `repoId` via `RepoIdNormalizer`.

---

## REST API

All endpoints on `localhost:19380` (configurable). Provided for tool integration (TerminalHost, custom tools, CI/CD).

### Eidet Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/eidet/context?repo={repoId}` | L0+L1 context block |
| `GET` | `/api/eidet/recall?repo={repoId}&q={query}` | Recall memories (hybrid search). Legacy alias: `/api/eidet/search`. |
| `GET` | `/api/eidet/{id}` | Get single memory |
| `GET` | `/api/eidet/stats?repo={repoId}` | Counts by type/layer |
| `POST` | `/api/eidet` | Store memory |
| `DELETE` | `/api/eidet/{id}` | Soft-delete |
| `GET` | `/api/eidet/layers?repo={repoId}` | List mounted layers |
| `POST` | `/api/eidet/layers/mount` | Mount layer |
| `DELETE` | `/api/eidet/layers/{layerId}` | Unmount layer |
| `GET` | `/api/eidet/links?repo={repoId}` | List cross-repo links |
| `POST` | `/api/eidet/links` | Create link |
| `POST` | `/api/eidet/intake?repo={repoId}` | Trigger intake |
| `POST` | `/api/eidet/packs/export` | Export .eidet pack |
| `POST` | `/api/eidet/packs/import` | Import .eidet pack |

### Service Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/health` | Health check (service discovery) |
| `GET` | `/api/status` | Version, uptime, memory stats |
| `GET` | `/api/config` | Read configuration |
| `PUT` | `/api/config` | Update configuration |
| `POST` | `/api/maintenance` | Trigger maintenance run — `200` with the report, or `202` + run id past the 30s grace window |
| `GET` | `/api/maintenance/runs/{id}` | Poll a run started by the above (admin scope; results kept 1h) |
| `POST` | `/api/config/enrichment/reload` | Reapply enrichment config on the running service (admin scope) |

---

## Rich TUI / Interactive CLI

The CLI is built for both humans (interactive TUI) and AI agents (structured output). Every command that asks questions also accepts flags for non-interactive use.

### `eidet setup` — First-Time Configuration

Interactive wizard that:

1. **Detects RavenDB** — checks localhost:8080 and common ports
   - If found: suggests using it, offers to create database
   - If not found: offers embedded mode or installation help
2. **Tests RavenDB connection** — creates test document, verifies vector search works
3. **Detects enrichment backends** — probes both Ollama (localhost:11434, `/api/version`) and OpenAI-compatible servers such as LM Studio (localhost:1234, `/v1/models`), plus the currently configured URL
   - Exactly one found: suggests enabling it; a tie asks which to use (non-interactive prefers Ollama)
   - OpenAI-compatible servers get their model picked from the live `/v1/models` list (a model id from the server is required for it to work at all)
   - None found: explains benefits, offers skip (enrichment is optional; `eidet enrichment setup` configures it later)
4. **Configures MCP** — detects Claude Code, Claude Desktop, offers to add MCP config
5. **Installs service** — registers as system service, starts it
6. **Runs first intake** — if a project directory was provided, runs intake immediately
7. **Summary** — shows what was configured, next steps

```bash
# Interactive (default)
eidet setup

# Non-interactive (for AI agents / scripts)
eidet setup --non-interactive \
  --raven-url "http://localhost:8080" \
  --ollama-url "http://localhost:11434" \
  --install-service \
  --configure-claude-code
```

### `eidet doctor` — Connection Testing & Troubleshooting

Tests all connections and reports issues with actionable fix instructions:

```bash
eidet doctor

  ┌─────────────────────────────────────────┐
  │  Eidet Health Check                     │
  ├─────────────────────────────────────────┤
  │                                         │
  │  Service      ✓ Running (PID 12345)     │
  │  RavenDB      ✓ Connected (v7.2.1)     │
  │    Database   ✓ "Eidet" (47 memories)   │
  │    Index      ✓ Memories/Search (clean)  │
  │    Embeddings ✓ bge-micro-v2 configured │
  │  Ollama       ✓ Connected (gemma4)      │
  │  MCP          ✓ Claude Code configured  │
  │  API          ✓ localhost:19380         │
  │                                         │
  │  All checks passed                      │
  └─────────────────────────────────────────┘
```

When something fails:
```
  │  RavenDB      ✗ Connection refused      │
  │                                         │
  │  Fix: RavenDB is not running.           │
  │  → Start it: safe process start raven   │
  │  → Or switch to embedded:               │
  │    eidet config set storage.mode embedded│
```

### `eidet status` — Service Overview

```bash
eidet status

  Eidet v1.0.0
  Status: Running (PID 12345, uptime 2h 15m)
  Storage: External RavenDB at localhost:8080
  Database: Eidet (47 memories across 3 repos)
  Ollama: Connected (gemma4)
  API: http://localhost:19380
  MCP: Claude Code configured
```

### Structured Output (for AI agents)

Every command supports `--json` for machine-readable output:

```bash
eidet status --json
{
  "version": "1.0.0",
  "status": "running",
  "pid": 12345,
  "storage": { "mode": "external", "url": "http://localhost:8080", "database": "Eidet" },
  "memories": { "total": 47, "repos": 3 },
  "ollama": { "connected": true, "model": "gemma4" },
  "api": { "url": "http://localhost:19380" }
}
```

### `eidet instructions` — CLAUDE.md Management

```bash
# Install usage instructions into ~/.claude/CLAUDE.md
eidet instructions install

# Show what would be added (dry-run)
eidet instructions show

# Remove instructions
eidet instructions remove
```

---

## Docker Container Integration

First-class support for running AI agents inside Docker containers that need to reach the Eidet service on the host.

### The Problem

AI coding agents increasingly run in containers (VS Code devcontainers, custom Docker setups, Codespaces). The Eidet service runs on the host. The container needs to reach it.

### Solution 1: Host Network Access (Simplest)

```bash
# docker-compose.yml or devcontainer.json
# Forward the Eidet port
"forwardPorts": [19380]

# Inside container, Eidet is reachable at:
# http://host.docker.internal:19380  (Docker Desktop)
# http://172.17.0.1:19380            (Linux Docker)
```

### Solution 2: Eidet MCP Bridge Inside Container

Install the `eidet` CLI inside the container. It auto-detects the containerized environment and connects to the host:

```dockerfile
# In Dockerfile or devcontainer.json postCreateCommand
RUN curl -fsSL https://get.eidet.dev | bash
```

The bridge detects it's in a container via:
1. Presence of `/.dockerenv` or `/run/.containerenv`
2. Checks `host.docker.internal` first, falls back to gateway IP
3. Tests connection and reports clear errors if host is unreachable

### Solution 3: Docker Helper Commands

`eidet docker` subcommand provides ready-to-use Docker configurations:

```bash
# Show docker-compose snippet for forwarding
eidet docker compose-snippet

# Show devcontainer.json additions
eidet docker devcontainer-snippet

# Test connectivity from inside a running container
docker exec <container> eidet doctor --from-container

# Show troubleshooting steps for common Docker networking issues
eidet docker troubleshoot
```

### Troubleshooting Guide (Built-In)

```bash
eidet docker troubleshoot

  Docker Container → Eidet Host Connectivity
  ──────────────────────────────────────────

  1. Check if Eidet is running on host:
     $ eidet status

  2. Check if port 19380 is accessible from container:
     $ curl http://host.docker.internal:19380/api/health

  3. If using Linux Docker (not Docker Desktop):
     host.docker.internal may not work. Use gateway IP instead:
     $ ip route | grep default | awk '{print $3}'
     Then: curl http://<gateway-ip>:19380/api/health

  4. If using custom Docker network:
     Add to docker-compose.yml:
       extra_hosts:
         - "host.docker.internal:host-gateway"

  5. If firewall is blocking:
     Eidet binds to 127.0.0.1 by default.
     For container access, bind to 0.0.0.0:
     $ eidet config set service.bindAddress "0.0.0.0"
     ⚠ Only do this on trusted networks.
```

---

## Installation

### One-Command Install

```bash
# Windows (PowerShell)
winget install eidet
# or: dotnet tool install -g eidet

# macOS
brew install eidet

# Linux
curl -fsSL https://get.eidet.dev | bash
```

### Service Registration

```bash
eidet install
```

This command:
1. Copies the binary to well-known location (`~/.eidet/bin/`)
2. Registers as system service:
   - **Windows**: Windows Service via `sc.exe create`
   - **macOS**: `launchd` plist in `~/Library/LaunchAgents/`
   - **Linux**: `systemd` unit in `~/.config/systemd/user/`
3. Starts the service
4. Creates default configuration at `~/.eidet/config.json`
5. Prompts to run `eidet setup` for first-time configuration

### Uninstall

```bash
eidet uninstall
# Stops service, removes registration
# Data preserved at ~/.eidet/data/ (use --purge to delete)
```

---

## Data Directory

```
~/.eidet/                     (or %APPDATA%\Eidet\ on Windows)
├── bin/
│   ├── eidet.exe             ← Thin launcher
│   ├── current/              ← Active version
│   └── previous/             ← Rollback version
├── data/
│   ├── raven/                ← RavenDB embedded data (only if embedded mode)
│   ├── events.log            ← Local event log (future: for sync)
│   └── packs/                ← .eidet pack files
├── config.json               ← Service configuration
└── logs/
    └── eidet-YYYY-MM-DD.log
```

---

## Configuration

```json
{
  "service": {
    "port": 19380,
    "bindAddress": "127.0.0.1"
  },
  "storage": {
    "mode": "external",
    "ravenUrl": "http://localhost:8080",
    "databaseName": "Eidet"
  },
  "memory": {
    "l1Count": 20,
    "l1MaxTokens": 500,
    "duplicateThreshold": 0.92,
    "vectorSimilarityMinimum": 0.70,
    "observationRetentionDays": 90,
    "autoIntakeOnFirstSession": true,
    "crossRepoRecallEnabled": true,
    "stalenessWarningDays": 7,
    "recallCacheEnabled": true
  },
  "maintenance": {
    "intervalHours": 24,
    "consolidationIntervalHours": 6,
    "atLocalTime": "03:00"
  },
  "enrichment": {
    "ollamaEnabled": false,
    "ollamaUrl": "http://localhost:11434",
    "ollamaModel": "gemma4",
    "autoOneLiner": true,
    "autoForesight": true,
    "autoConsolidation": true
  },
  "updates": {
    "autoCheck": true,
    "autoInstall": false,
    "channel": "stable"
  }
}
```

Configuration can be modified via:
- `eidet setup` (interactive TUI)
- `eidet config set <key> <value>` (CLI)
- Direct file edit
- `PUT /api/config` (REST API)

---

## Dependency Philosophy

**Principle**: Minimal dependencies, but don't reinvent proven solutions.

### Allowed Dependencies

| Dependency | Why | Notes |
|------------|-----|-------|
| `RavenDB.Client` | Core storage, vector search, full-text | The foundation. No alternative offers hybrid search in one round-trip. |
| `RavenDB.Embedded` | Zero-setup mode | Only loaded when `storage.mode = "embedded"`. Optional. |
| `System.Text.Json` | JSON serialization | Microsoft, already in .NET runtime. |
| `Microsoft.Extensions.Hosting` | Service hosting, DI, configuration | Standard .NET service host. |
| `Microsoft.Extensions.Http` | HttpClient factory | For Ollama, future sync. |
| `Spectre.Console` | Rich TUI rendering | Best .NET TUI library. Considered essential for the setup/doctor UX. |

### Explicitly Avoided

| Don't Use | Why | Alternative |
|-----------|-----|-------------|
| Entity Framework | Over-engineered for document store | RavenDB client directly |
| Serilog / NLog | Overkill for service logging | `Microsoft.Extensions.Logging` |
| AutoMapper | Mapping overhead for small model | Manual mapping (explicit, debuggable) |
| MediatR | CQRS overhead unnecessary | Direct service calls |
| Polly | Retry complexity not needed | Simple retry loops where needed |
| gRPC | Premature for local communication | REST + JSON (simpler, debuggable) |
| SignalR (client) | Future sync only | Defer until sync phase |

### Dependency Review Criteria

Before adding any dependency, answer:
1. Does it solve a real problem we have NOW (not hypothetical)?
2. Is the .NET runtime or Microsoft.Extensions equivalent sufficient?
3. What's the maintenance/security track record?
4. What's the transitive dependency footprint?
5. Can we vendor or isolate it if it becomes abandoned?

---

## Update System

The tool ships as a dotnet global tool, so the package manager owns the bits and there is no
launcher, staging directory, or version tree of our own. What we own is *when* to update, *whether*
a given release is safe to take unattended, and how the news reaches a human who never runs the CLI.

### Checking and telling are separate from installing

One caller reaches the network: the nightly `UpdateCheck` scheduled task. It writes what it learned
to `~/.eidet/update-check.json`, and every surface that wants to *mention* an update reads that
file. Without the split, each CLI invocation and each MCP session start would pay a NuGet
round-trip to decide whether to print one line it usually won't print.

The notice is rationed to once per process (`UpdateNotice`), so the CLI, `eidet_context`, and the
TUI can each ask without coordinating. Having nothing to say does not spend the ration — a service
that learns about a release at 04:00 still gets to mention it afterwards. `eidet mcp` never prints
it at all: stdout there is the JSON-RPC channel.

### Deciding to install

Automation is opt-in (`update.autoUpdate`, asked once in `eidet setup`) and runs at a configured
local hour rather than on an interval, because "every 24h" drifts to whenever the service last
restarted — which is the middle of a working day for a tool that replaces its own binary.

Three rules decide whether a found release is actually taken:

- **Age gate.** Nothing younger than `update.minimumAgeHours` (default 24). Releases are immutable,
  so a bad build can only be superseded, never fixed in place; the window is what lets the
  successor exist before the fleet moves. A publish date we cannot read counts as too young.
- **SemVer, not equality.** A locally built or pre-release binary is *ahead* of NuGet's latest.
  Treating "different" as "outdated" is a silent downgrade at 04:00.
- **Install flavor.** Only a `dotnet tool` install can replace itself. Container and standalone
  installs get the notice and nothing else. The detector leans towards "standalone" on doubt:
  guessing that way costs a notice-only night, the other way schedules a doomed
  `dotnet tool update`.

### Installing

The scheduler shells out to the same `eidet update` a human would type, with the vetted version
pinned (`--to`), and it persists its own task state *first* — the updater's first act is to stop
the service that called it. Platform-specific work stays in one place: the Windows trampoline
(a detached script that waits for file locks to release, retries against MCP clients that respawn
`eidet mcp`, verifies the installed version, and restarts the service), and a direct replace on
macOS/Linux where loaded files are not locked.

Rollback is `eidet update --rollback`: reinstall the version recorded in `version-history.json`
before this one. Exact and reproducible *because* releases are immutable — the predecessor is
guaranteed to still exist and to be the same bytes. There is no automatic post-install health
revert; the age gate is the fleet-level circuit breaker and rollback is the manual escape hatch.

Authenticity comes from the ecosystem rather than from us: NuGet publishing uses OIDC trusted
publishing from the release workflow, and NuGet.org validates the repository signature on install.
See `STRIDE.md` T-3.

---

## CLI Interface

The `eidet` binary serves dual purpose: CLI tool (for humans) and MCP bridge (for AI clients).

### Management Commands

```bash
eidet install              # Install and start service
eidet uninstall            # Stop and remove service
eidet setup                # Interactive first-time configuration (TUI)
eidet doctor               # Connection testing and troubleshooting
eidet status               # Service status + stats
eidet update               # Check for and apply updates
eidet update --check       # Report only, install nothing
eidet update --to <ver>    # Install one specific version
eidet update --rollback    # Reinstall the previously installed version
eidet config get <key>     # Read config value
eidet config set <key> <v> # Write config value
```

### Memory Commands (shortcuts for REST API)

```bash
eidet recall "auth patterns"           # Recall memories
eidet store -t insight "CQRS pattern"  # Store a memory
eidet stats                            # Memory counts
eidet export -o memories.md            # Export as markdown
eidet intake /path/to/project          # Run intake
eidet maintain                         # Trigger maintenance
```

### Docker & Integration Commands

```bash
eidet docker                           # Docker/devcontainer integration guide
eidet docker --json                    # Machine-readable Docker config
eidet instructions                     # Print memory usage instructions (stdout)
eidet instructions --install           # Append to ~/.claude/CLAUDE.md
eidet instructions --project           # Create in project CLAUDE.md
```

### Enrichment Configuration

```bash
eidet enrichment setup                 # Interactive wizard: detect Ollama/LM Studio, pick
                                       # provider + URL + model (from the backend's live model
                                       # list), save all keys atomically, offer live-apply
eidet enrichment reload                # Tell the running service to reapply the Enrichment
                                       # config section without a restart (POST
                                       # /api/config/enrichment/reload; --api-key <KEY> when
                                       # auth is enabled — requires admin scope)
```

The wizard exists so the four coupled keys (`enrichment.enabled/provider/url/model`) can never
end up half-edited the way sequential `eidet config set` calls can. Live reload swaps the
enrichment adapter, retargets the health monitor probe, and starts the enrich-on-store worker
if enrichment just became enabled; drift-review/reflection settings still need a restart.

### Ollama Management

```bash
eidet ollama status                    # Show Ollama connection and models
eidet ollama list                      # List installed models
eidet ollama pull gemma4               # Pull a model with progress bar
```

### MCP Bridge

```bash
eidet mcp                          # Start stdio MCP bridge
eidet mcp --workdir /path/to/proj  # With explicit working directory
```

### Environment Variables

Environment variables override config file values (useful for containers and CI/CD):

| Variable | Overrides | Example |
|----------|-----------|---------|
| `EIDET_API_URL` | `service.bindAddress` + `service.port` | `http://host.docker.internal:19380` |
| `EIDET_RAVEN_URL` | `storage.ravenUrl` | `http://ravendb:8080` |
| `EIDET_OLLAMA_URL` | `enrichment.ollamaUrl` | `http://ollama:11434` |
| `EIDET_OLLAMA_MODEL` | `enrichment.ollamaModel` | `gemma4` |

---

## Relationship to TerminalHost

TerminalHost becomes a **rich client** of the Eidet service via the REST API:

```
Before (embedded):
  TerminalHost → MemoryHostService → RavenMemoryStore → local RavenDB

After (service):
  TerminalHost → EidetClient → Eidet Service REST API → RavenDB
```

**TerminalHost's MemoryHostService changes to**:
1. Detect if Eidet service is running (`GET /api/health`)
2. If running: proxy all operations to REST API
3. If not running: show "install Eidet" prompt (or optionally start it)
4. Memory Browser, Settings, Debug Log → all talk to service API

**Migration** for existing TerminalHost users:
1. Settings → Memory → "Migrate to Eidet Service"
2. Exports all memories, imports into Eidet service via REST
3. Reconfigures TerminalHost to use service API
4. Old embedded RavenDB data preserved as backup

---

## Service Discovery

Clients find the running service via:

1. **Well-known port**: `localhost:19380` (configurable)
2. **Health endpoint**: `GET /api/health` returns `{"status": "ok", "version": "1.0.0"}`
3. **Lock file**: `~/.eidet/service.lock` (PID + port)
4. **Named pipe** (Windows): `\\.\pipe\eidet-service`
5. **Unix socket** (macOS/Linux): `~/.eidet/eidet.sock`

---

## Resource Usage

| Resource | Target | Notes |
|----------|--------|-------|
| Memory (idle, external RavenDB) | < 30 MB | Just the service, no embedded DB |
| Memory (idle, embedded RavenDB) | < 80 MB | Service + RavenDB embedded |
| Memory (active) | < 200 MB | During search/indexing |
| Disk (binary) | ~15 MB | Without embedded RavenDB |
| Disk (binary + embedded) | ~100 MB | With embedded RavenDB engine |
| CPU (idle) | < 1% | Waiting for requests |

---

## Security

- **Localhost-only by default**: REST API binds to `127.0.0.1`. No external exposure.
- **No auth for local access**: Same trust model as RavenDB, Docker — local access = trusted.
- **Secret scanning gate**: Runs before ANY storage. Blocks API keys, tokens, passwords, private keys.
- **Signal gate**: Blocks empty, trivial, and agent self-talk content.
- **No data exfiltration**: Nothing leaves the machine. No analytics, no telemetry, no phone-home (update checks are opt-in and metadata-only).
- **Write gates always active**: Cannot be disabled via configuration.

### API Key Authentication

For programmatic access, CI/CD, or when binding to non-localhost:

```bash
# Create an API key (auto-enables auth)
eidet api-key create "GitHub Actions" --scopes "write:observations,read:all"
# → Shows key once: eidet_abc123...

# List keys
eidet api-key list

# Revoke a key
eidet api-key revoke <id>
```

API keys are scoped:
- `read:all` — read any memory
- `write:observations` — store observations and intake (CI/CD use case)
- `write:all` — store any type, consolidate, export
- `admin` — maintenance, config changes (implies all other scopes)

Keys are stored in `config.json` as SHA256 hashes. The raw key is shown once at creation.

Use in requests:
```bash
curl -H "Authorization: Bearer eidet_abc123..." http://localhost:19380/api/eidet/search?repo=...
```

Health (`/api/health`) and status (`/api/status`) endpoints are always public (no auth required).

### Network Binding Guard

When binding to a non-localhost address (e.g., `0.0.0.0` for container access), `eidet serve` requires auth to be enabled:

```bash
# This will fail:
eidet config set service.bindAddress 0.0.0.0
eidet serve  # Error: binding to non-localhost without auth

# Fix: create a key first
eidet api-key create "network-access"
eidet serve  # Now works — auth enabled

# Or disable the guard (not recommended):
eidet config set auth.requireForNonLocalhost false
```

### CORS

The REST API includes CORS headers (`Access-Control-Allow-Origin: *`) to support browser-based clients (Web UI). Preflight `OPTIONS` requests return 204 with appropriate headers.

---

## Logging

Structured logging to `~/.eidet/logs/`:
- Rotated daily, 7-day retention
- Levels: Error, Warning, Info, Debug
- Configurable via `config.json` or `EIDET_LOG_LEVEL` environment variable
