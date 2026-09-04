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
| `maintenance.atLocalTime` | string | `03:00` | Local wall-clock time the nightly pass is anchored to |

The anchor is what keeps the pass where you put it. Runs are scheduled on a grid anchored to
`atLocalTime`, not at "whenever the last one finished plus `intervalHours`" — a pass that takes two
hours would otherwise start two hours later every day and walk into the working day. A long or missed
run costs its own slot and nothing more.

### update

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `update.check` | bool | `true` | Look for new releases once a night, and mention one when it exists |
| `update.autoUpdate` | bool | `false` | Install what it finds, without asking. `eidet setup` asks once |
| `update.atLocalTime` | string | `04:00` | Local wall-clock time for the nightly check |
| `update.minimumAgeHours` | int | `24` | Refuse to auto-install a release younger than this |

Checking and installing are separate switches. With `autoUpdate` off you still get told a new
version exists — once per process, on the CLI, in `eidet_context`, and as a banner in the Web UI —
and install it yourself with `eidet update`.

Only the nightly task reaches NuGet; everything else reads its cached answer, so an MCP session
start never pays a network round-trip. Nothing is checked at all with `update.check` set to false.

`minimumAgeHours` exists because releases are immutable: a bad build can only be superseded, never
replaced, so waiting a day leaves room to publish the fix before the fleet takes the bad one. A
release whose publish date can't be read is treated as too young rather than old enough.

Auto-update applies only to `dotnet tool` installs. Container and standalone-binary installs are
replaced as a whole, so they get the notice and nothing else.

```bash
eidet update --check          # ask now, print the answer, install nothing
eidet update                  # install the newest release
eidet update --to 0.11.2      # install one specific version
eidet update --rollback       # go back to the previously installed version
```

### enrichment

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `enrichment.ollamaEnabled` | bool | `false` | Enable Ollama enrichment |
| `enrichment.ollamaUrl` | string | `http://localhost:11434` | Ollama API URL |
| `enrichment.ollamaModel` | string | `gemma4` | Model for enrichment tasks |
| `enrichment.apiKey` | string | — | Bearer token sent with every request. For a private network cluster behind a gateway; local servers ignore it. `config get`/`list` print `(set)`, never the value |
| `enrichment.thinking` | bool | *unset* | `false` turns a reasoning model's thinking off (`chat_template_kwargs.thinking` on vLLM/llama.cpp, `think` on Ollama) — ~5x cheaper per call. Unset sends nothing; `default` unsets |
| `enrichment.fallbacks` | array | `[]` | Backends tried in order when the one before is offline or fails a call — each with `provider`, `url`, `model`, optional `apiKey`, `thinking`. Edit in the file or via `eidet enrichment setup` |
| `enrichment.autoOneLiner` | bool | `true` | Auto-generate one-liner summaries |
| `enrichment.autoForesight` | bool | `true` | Auto-generate foresight hints |
| `enrichment.autoConsolidation` | bool | `true` | Use LLM for consolidation merges |
| `enrichment.driftReview.enabled` | bool | `true` | Nightly LLM re-read of stored memories for staleness |
| `enrichment.driftReview.nightlyBatch` | int | `25` | Model calls per repo per run — the recurring cost |
| `enrichment.driftReview.minAgeDays` | int | `7` | Ignore memories younger than this |
| `enrichment.driftReview.reviewIntervalDays` | int | `90` | How long a verdict stands before re-review. `0` = every night |
| `enrichment.driftReview.minModelConfidence` | float | `0.7` | Below this a verdict is recorded but not acted on |
| `enrichment.driftReview.autonomy` | string | `Decay` | `FlagOnly`, `Decay`, or `Expire` |
| `enrichment.reflection.enabled` | bool | `false` | Mint new memories from feedback residue (dormant) |

Drift review is the only enrichment surface whose cost recurs: it is `nightlyBatch` model calls per
repo per night. `reviewIntervalDays` is what makes it converge — a memory drops out of the candidate
set until its verdict ages past the interval, so a corpus nobody is touching costs nothing. Setting it
to `0` restores an unbounded nightly sweep. The startup banner's `Nightly AI:` line reports what the
running service will actually spend.

A private network model in front of a local one is the intended shape for `fallbacks`: the network
cluster is fast but not always reachable, the local server always is. Each backend keeps its own
health verdict (cached five minutes, probed with a five-second budget), so an offline primary costs
one probe per five minutes and every call goes straight to the fallback. `eidet doctor` shows one row
per backend; the service log names the backend that answers and reports when a fallback takes over.
The URL may be written with or without a trailing `/v1` — both mean the same server.

```json
{
  "enrichment": {
    "enabled": true,
    "provider": "OpenAiCompatible",
    "url": "https://cortex.example.com",
    "model": "deepseek-v4-flash",
    "apiKey": "…",
    "thinking": false,
    "fallbacks": [
      { "provider": "Ollama", "url": "http://localhost:11434", "model": "gemma4" }
    ]
  }
}
```

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
  "update": {
    "check": true,
    "autoUpdate": false,
    "atLocalTime": "04:00",
    "minimumAgeHours": 24
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
