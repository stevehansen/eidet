---
title: Configuration
nav_order: 7
---

# Configuration

Eidet stores its configuration in a JSON file at:

| Platform | Path |
|----------|------|
| Windows | `%APPDATA%\Eidet\eidet.json` |
| macOS/Linux | `~/.eidet/eidet.json` |

## Managing Config

```bash
# View all settings
eidet config list

# Get a single value
eidet config get storage.ravenUrl

# Set a value
eidet config set service.port 19380

# JSON output
eidet config list --json
```

## Environment Variable Overrides

Environment variables take precedence over the config file:

| Variable | Config path | Example |
|----------|-------------|---------|
| `EIDET_API_URL` | `service.bindAddress` + `service.port` | `http://0.0.0.0:19380` |
| `EIDET_RAVEN_URL` | `storage.ravenUrl` | `http://ravendb:8080` |
| `EIDET_OLLAMA_URL` | `enrichment.ollamaUrl` | `http://ollama:11434` |
| `EIDET_OLLAMA_MODEL` | `enrichment.ollamaModel` | `gemma4` |

These are useful for Docker and CI/CD environments.

## Config Reference

### service

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `service.port` | int | `19380` | HTTP port for REST API and MCP |
| `service.bindAddress` | string | `127.0.0.1` | Bind address (use `0.0.0.0` for network access) |

### storage

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `storage.mode` | enum | `External` | `External` or `Embedded` |
| `storage.ravenUrl` | string | `http://localhost:8080` | RavenDB connection URL |
| `storage.databaseName` | string | `Eidet` | RavenDB database name |
| `storage.dataDir` | string | null | Data directory for embedded mode |

### memory

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `memory.l1Count` | int | `20` | Max memories in L1 context |
| `memory.l1MaxTokens` | int | `500` | Max tokens for L1 context |
| `memory.duplicateThreshold` | float | `0.92` | Vector similarity threshold for duplicate detection |
| `memory.vectorSimilarityMinimum` | float | `0.70` | Minimum similarity for vector search results |
| `memory.observationRetentionDays` | int | `90` | Days before old observations are cleaned up |
| `memory.autoIntakeOnFirstSession` | bool | `true` | Auto-ingest project files on first MCP session |
| `memory.crossRepoRecallEnabled` | bool | `true` | Include cross-repo results in recall |
| `memory.stalenessWarningDays` | int | `7` | Days before adding staleness warnings |
| `memory.recallCacheEnabled` | bool | `true` | Cache recall results (5-min TTL) |

### maintenance

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `maintenance.intervalHours` | int | `24` | Hours between maintenance runs |
| `maintenance.consolidationIntervalHours` | int | `6` | Hours between consolidation runs |

### enrichment

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `enrichment.ollamaEnabled` | bool | `false` | Enable Ollama enrichment |
| `enrichment.ollamaUrl` | string | `http://localhost:11434` | Ollama API URL |
| `enrichment.ollamaModel` | string | `gemma4` | Model for enrichment tasks |
| `enrichment.autoOneLiner` | bool | `true` | Auto-generate one-liner summaries |
| `enrichment.autoForesight` | bool | `true` | Auto-generate foresight hints |
| `enrichment.autoConsolidation` | bool | `true` | Use LLM for consolidation merges |

### auth

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `auth.enabled` | bool | `false` | Require API key authentication |
| `auth.requireForNonLocalhost` | bool | `true` | Block non-localhost without auth |

Manage API keys:

```bash
eidet api-key create "my-key" --scopes read:all,write:all
eidet api-key list
eidet api-key revoke <key-id>
```

Scopes: `read:all`, `write:observations`, `write:all`, `admin` (implies all).

### hooks

Hooks are configured in the config file directly (not via `config set`). See [Hooks]({% link hooks.md %}) for details.

```json
{
  "hooks": {
    "preStore": [
      { "command": "python validate.py", "timeoutSeconds": 10, "enabled": true }
    ],
    "postStore": [],
    "preRecall": [],
    "postRecall": [],
    "preForget": [],
    "postForget": []
  }
}
```

## Example Config File

```json
{
  "service": {
    "port": 19380,
    "bindAddress": "127.0.0.1"
  },
  "storage": {
    "mode": "Embedded",
    "ravenUrl": "http://localhost:8080",
    "databaseName": "Eidet"
  },
  "memory": {
    "l1Count": 20,
    "duplicateThreshold": 0.92,
    "autoIntakeOnFirstSession": true
  },
  "enrichment": {
    "ollamaEnabled": true,
    "ollamaModel": "gemma4"
  },
  "auth": {
    "enabled": false
  },
  "hooks": {
    "preStore": [],
    "postStore": []
  }
}
```
