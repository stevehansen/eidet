# Eidet - STRIDE Threat Model

**Version:** 1.0
**Created:** 2026-04-12
**Author:** Steve Hansen
**Next Review:** 2027-04-12

---

## 1. System Overview

### 1.1 Application Description

Eidet is a local-first long-term memory service for AI coding agents. It provides persistent, semantic memory across coding sessions using a local RavenDB database (embedded or external), optional Ollama LLM enrichment, and exposes functionality through a REST API, MCP server (stdio + HTTP), CLI commands, and an embedded Web UI.

### 1.2 User Types

| User | Description | Access |
|------|-------------|--------|
| AI Agent (MCP) | Claude Code, Cursor, etc. connecting via MCP stdio or HTTP | Full tool access (13 MCP tools) |
| AI Agent (REST) | Any HTTP client using the REST API | API key scoped (read/write/admin) |
| Developer (CLI) | Local user running `eidet` commands | Full access via CLI |
| Developer (Web UI) | Browser user at `http://localhost:19380/ui` | Read-only browse + admin actions |
| Ollama Service | Local LLM for optional enrichment | Receives memory content in prompts |

### 1.3 Components

```
+------------------+     +-------------------+     +------------------+
|   AI Agents      |     |   Web Browser     |     |   CLI User       |
| (MCP/REST/SDK)   |     | (localhost:19380)  |     | (eidet commands) |
+--------+---------+     +---------+---------+     +--------+---------+
         |                         |                         |
         v                         v                         v
+--------+-------------------------+-------------------------+---------+
|                     Eidet Service (eidet serve)                      |
|  +--------------------+  +------------------+  +------------------+  |
|  | EidetApiServer     |  | McpServer        |  | CLI Commands     |  |
|  | (HttpListener)     |  | (stdio/HTTP)     |  | (Spectre.Console)|  |
|  +----+---------------+  +--------+---------+  +--------+---------+  |
|       |                           |                      |           |
|  +----+---------------------------+----------------------+---------+ |
|  |                     MemoryService                               | |
|  |  WriteGate (SecretScanner + SignalGate) | HookRunner            | |
|  +----+----------------------+------------------+---------+--------+ |
|       |                      |                  |         |          |
|  +----+------+    +----------+--------+  +------+---+  +-+--------+ |
|  | RavenDB   |    | OllamaEnrichment  |  | Backup   |  | Export   | |
|  | (Embedded |    | Service           |  | Service  |  | Service  | |
|  |  /External)|   +-------------------+  +----------+  +----------+ |
+------+--------+-------------+------------------------------------+---+
       |                      |
       v                      v
+------+--------+    +--------+---------+
| RavenDB 7.x   |    | Ollama (local)   |
| localhost:8080 |    | localhost:11434   |
+----------------+    +------------------+
```

### 1.4 Trust Boundaries

| Boundary | Description |
|----------|-------------|
| **TB-1: Network ↔ API** | HTTP requests entering EidetApiServer (localhost:19380) |
| **TB-2: Network ↔ MCP HTTP** | JSON-RPC over HTTP POST to /mcp endpoint |
| **TB-3: Stdio ↔ MCP** | AI agent process communicating via stdin/stdout |
| **TB-4: Service ↔ RavenDB** | HTTP to external RavenDB or in-process embedded calls |
| **TB-5: Service ↔ Ollama** | HTTP to local Ollama service with memory content in prompts |
| **TB-6: Service ↔ Filesystem** | Config, backups, pack files, intake of project files |
| **TB-7: Service ↔ Hook Processes** | Spawning external processes defined in config |
| **TB-8: Service ↔ GitHub API** | HTTPS to api.github.com for self-update |
| **TB-9: Browser ↔ Web UI** | Embedded SPA served over HTTP, makes API calls |

### 1.5 Data Classification

| Category | Examples | Sensitivity |
|----------|----------|-------------|
| Memory Content | Developer knowledge, code patterns, architecture notes | Medium — may contain proprietary code insights |
| API Keys | SHA256 hashes in config.json, raw keys shown once | High — grants API access |
| Repository Paths | Normalized to repo IDs (e.g., `P--Eidet`) | Low — filesystem layout info |
| Configuration | RavenDB URL, Ollama URL, bind address, hook commands | Medium — service topology |
| Backup Files | Full RavenDB export in .eidetbackup ZIP | High — contains all memories |
| Pack Files | Exported Packs in .eidet (markdown/JSON) | Medium — portable memory sets |
| Enrichment Data | Ollama-generated summaries, foresight hints, entities | Low — derived from content |

---

## 2. STRIDE Analysis

### 2.1 Spoofing

| ID | Threat | Attack Path | Likelihood | Impact | Score | Mitigation |
|----|--------|-------------|:----------:|:------:|:-----:|------------|
| S-1 | API key interception | Attacker sniffs Bearer token over unencrypted HTTP on non-localhost network | 2 | 4 | **8** | Auth only required for non-localhost by default. Localhost traffic not typically interceptable. For remote access, deploy behind TLS reverse proxy. |
| S-2 | Unauthenticated localhost access | Any local process can call the API without auth when auth is disabled (default) | 3 | 2 | 6 | By design — localhost-only binding assumed trusted. Auth can be enabled via `eidet api-key create`. |
| S-3 | MCP stdio spoofing | Malicious process hijacks stdio pipe to impersonate AI agent | 1 | 3 | 3 | MCP stdio is launched by the AI client itself. Process-level isolation is the OS responsibility. |
| S-4 | CORS-based credential theft | Malicious website makes cross-origin API calls using user's browser session | 2 | 3 | 6 | API uses Bearer tokens (not cookies), so CORS `*` does not leak credentials. However, unauthenticated endpoints (health, status, UI) are accessible cross-origin. |
| S-5 | Auth guard bypass via env var | Attacker sets `EIDET_AUTH_REQUIRE_NONLOCALHOST=false` to disable network auth requirement | 1 | 4 | 4 | Requires ability to set environment variables in the service process — implies existing system compromise. |

**Countermeasures in place:**
- API key auth with SHA256 hashing and scope model (ApiKeyService.cs)
- Network binding guard refuses non-localhost without auth enabled (ServeCommand.cs)
- Default bind to `127.0.0.1` only (EidetConfig.cs)
- Cryptographically random key generation using `RandomNumberGenerator` (ApiKeyService.cs:19)

### 2.2 Tampering

| ID | Threat | Attack Path | Likelihood | Impact | Score | Mitigation |
|----|--------|-------------|:----------:|:------:|:-----:|------------|
| T-1 | Malicious pack import | Attacker crafts .eidet pack with forged importance scores, fake lineage chains, or memories targeting other repos | 2 | 3 | 6 | Pack imports are assigned to `bundle:{packId}` layer (read-only), non-local memories de-boosted 0.8x in recall scoring. No signature verification on pack files. |
| T-2 | Config file modification | Attacker modifies config.json to change RavenDB URL (exfiltrate data), add malicious hooks, or disable auth | 2 | 4 | **8** | Config file lives in user's AppData/home directory with OS-level file permissions. No integrity checking on config load. |
| T-3 | Unverified binary update | `eidet update` downloads binary from GitHub Releases over HTTPS but does not verify cryptographic signature | 2 | 4 | **8** | HTTPS provides transport security. GitHub's CDN provides some integrity. No GPG or checksum verification of downloaded binary. |
| T-4 | Backup tampering | Attacker modifies .eidetbackup manifest or contents | 1 | 3 | 3 | SHA256 checksum verified on restore (BackupService.cs:115). Tampering both data and manifest checksum in a consistent way requires understanding the format. |
| T-5 | Environment variable injection | Attacker sets `EIDET_RAVEN_URL` to redirect database traffic to attacker-controlled server | 1 | 4 | 4 | Requires process-level env var access. No URL validation on config load. |
| T-6 | Memory content manipulation via API | Authenticated attacker stores misleading memories to poison agent knowledge | 2 | 2 | 4 | Write gate blocks secrets and low-signal content. Scope model limits write access. Quality dashboard detects anomalies. |

**Countermeasures in place:**
- Write gate with SecretScanner (13 patterns) and SignalGate (WriteGate.cs)
- Pack imports isolated to read-only layers with de-boost scoring
- Backup SHA256 checksum verification
- API key scopes restrict write access
- Quality dashboard detects low-confidence and conflicting memories

### 2.3 Repudiation

| ID | Threat | Attack Path | Likelihood | Impact | Score | Mitigation |
|----|--------|-------------|:----------:|:------:|:-----:|------------|
| R-1 | Unattributed API access | No request-level logging — cannot determine who called which endpoint or when | 3 | 2 | 6 | No HTTP request logging middleware. API key ID is available but not logged per-request. |
| R-2 | Forget without audit | User forgets a memory and claims it never existed | 1 | 2 | 2 | Forget creates audit observation with reason and DerivedFrom link (MemoryService.cs:356-371). Soft-delete preserves ValidUntil timestamp. |
| R-3 | Hook execution unlogged | Hooks execute external commands with no persistent record of what ran or its output | 2 | 2 | 4 | Post-hook failures are silently swallowed. No execution log for pre-hook or post-hook invocations. |

**Countermeasures in place:**
- Soft-delete with validity intervals (append-only model)
- Forget audit observations with DerivedFrom links
- Memory version chain via `eidet_history`

### 2.4 Information Disclosure

| ID | Threat | Attack Path | Likelihood | Impact | Score | Mitigation |
|----|--------|-------------|:----------:|:------:|:-----:|------------|
| I-1 | Exception message leakage | API returns raw `ex.Message` in 500 responses, potentially exposing file paths, connection strings, or stack traces | 3 | 3 | **9** | Catch-all handler at EidetApi.cs:212-214 returns exception message directly. |
| I-2 | Unauthenticated status info | `/api/status` reveals version, uptime, database type, and memory statistics without authentication | 3 | 2 | 6 | By design for operational monitoring. Information value is low but aids reconnaissance. |
| I-3 | Memory content in Ollama prompts | User memory content sent to Ollama over plaintext HTTP for enrichment | 2 | 3 | 6 | Ollama runs locally (localhost:11434). Content not sent over network unless Ollama URL is changed. Enrichment is optional and can be disabled. |
| I-4 | Unencrypted backup files | .eidetbackup files contain full database export without encryption | 2 | 3 | 6 | Backups stored in user's AppData directory. File system permissions are the only protection. |
| I-5 | Config file contains service topology | config.json contains RavenDB URL, Ollama URL, bind address, API key hashes | 2 | 2 | 4 | Stored in user profile directory with OS-level file permissions. API key hashes cannot recover raw keys. |
| I-6 | Cross-origin information via CORS | CORS `Access-Control-Allow-Origin: *` allows any website to read API responses from unauthenticated endpoints | 2 | 2 | 4 | Health, status, and UI endpoints are public. With auth enabled, all data endpoints require Bearer token which browsers cannot automatically attach cross-origin. |

**Countermeasures in place:**
- API key auth with scope model for data endpoints
- Secret scanner prevents accidental storage of credentials
- Localhost-only default binding
- Embedded RavenDB runs in-process (no network exposure)
- XSS protection in Web UI via `escHtml()` function (app.js:599-606)
- Path traversal protection for embedded file serving (EidetApi.cs:605)

### 2.5 Denial of Service

| ID | Threat | Attack Path | Likelihood | Impact | Score | Mitigation |
|----|--------|-------------|:----------:|:------:|:-----:|------------|
| D-1 | No rate limiting | Attacker floods API with requests, exhausting RavenDB connections or CPU | 2 | 3 | 6 | No rate limiting on any endpoint. Mitigated by localhost-only default binding. |
| D-2 | Large request body | Attacker sends oversized JSON body to store endpoint, consuming memory | 2 | 2 | 4 | No explicit request size limit in HttpListener. RavenDB document size limits provide some protection. |
| D-3 | Expensive vector search | Attacker issues many concurrent vector similarity searches | 2 | 2 | 4 | RavenDB handles query load. 2x over-fetch for hybrid retrieval adds overhead. |
| D-4 | Hook process fork bomb | Malicious hook configuration spawns processes rapidly | 1 | 3 | 3 | Hooks have configurable timeout with process tree kill (HookRunner.cs:161). Only runs hooks configured by the user themselves. |
| D-5 | Embedded RavenDB disk exhaustion | Continuous storage fills embedded RavenDB data directory | 2 | 2 | 4 | Maintenance pipeline provides TTL expiry and observation retention. No hard disk quota enforcement. |

**Countermeasures in place:**
- Hook execution timeout with process tree kill
- Maintenance pipeline with TTL expiry, dedup sweep, importance decay
- Graceful shutdown on SIGTERM/Ctrl+C
- Default localhost-only binding limits attack surface

### 2.6 Elevation of Privilege

| ID | Threat | Attack Path | Likelihood | Impact | Score | Mitigation |
|----|--------|-------------|:----------:|:------:|:-----:|------------|
| E-1 | Arbitrary command execution via hooks | Attacker who gains config.json write access adds malicious hook commands | 2 | 4 | **8** | Hook commands execute arbitrary processes (HookRunner.cs:126). Requires config file write access, which implies existing local compromise. UseShellExecute=false prevents shell injection. |
| E-2 | File path traversal in pack/export | API accepts user-controlled file paths for pack import (`path`) and export (`outputPath`) without directory restriction | 2 | 3 | 6 | Can read .eidet files from or write to arbitrary filesystem locations (within process permissions). No directory whitelist. |
| E-3 | Prompt injection via Ollama | Malicious memory content manipulates Ollama prompt to produce crafted enrichment output | 2 | 2 | 4 | Ollama output stored as metadata (summaries, hints). Not executed. Write gate still validates enrichment-triggered stores. |
| E-4 | Cross-repo memory pollution | Authenticated user stores memories for repos they don't own | 2 | 2 | 4 | RepoId is caller-specified with no ownership verification. Local-only scope makes this low risk (all repos belong to same user). |
| E-5 | Install command runs system commands | `eidet install` executes schtasks.exe, launchctl, or systemctl | 1 | 3 | 3 | Commands are hardcoded (not user-controlled). Binary path is constructed from install directory. Runs at user privilege level, not elevated. |

**Countermeasures in place:**
- Hook processes run without shell (UseShellExecute=false, no command injection)
- Hook timeout with process tree kill
- Service installs at user privilege level (scheduled task, launchd agent, systemd user unit)
- RavenDB access via strongly-typed LINQ API (no injection)
- System.Text.Json for serialization (no type-based deserialization attacks)

---

## 3. Risk Summary

### 3.1 High Priority Threats (Score >= 8)

| ID | Threat | Score | Status |
|----|--------|:-----:|--------|
| S-1 | API key interception over unencrypted HTTP | 8 | Accepted — localhost default. Improve startup logging to show bind address and auth status. |
| T-2 | Config file modification (add hooks, change RavenDB URL, disable auth) | 8 | Accepted — attacker with config write access already has equivalent system access. Improve startup logging to show configured hooks and connection targets. |
| T-3 | Unverified binary update from GitHub | 8 | Accepted — immutable releases enabled on GitHub, HTTPS transport, public repo/actions. Improve update command to show full download URI. |
| I-1 | Exception message leakage in API 500 responses | 9 | To fix — return generic error messages instead of raw exception details. |
| E-1 | Arbitrary command execution via hook config | 8 | Accepted — same trust boundary as T-2. Hooks run at same privilege as user. Log configured hooks at startup. |

### 3.2 Residual Risks

- **No TLS termination**: Eidet uses HTTP only. Deployments beyond localhost require a reverse proxy (nginx, Caddy, Cloudflared) for TLS. Startup log shows bind address so the user is aware.
- **Config file as trust anchor**: Many threats reduce to "attacker can modify config.json." This is accepted — an attacker with that access already has full user-level system compromise.
- **No request audit log**: Cannot investigate after-the-fact who accessed what data.
- **Pack file integrity**: .eidet pack files have no cryptographic verification, allowing memory injection if attacker can place files.
- **Secret scanner coverage**: Regex-based patterns can be evaded with obfuscation, encoding, or unknown token formats.

---

## 4. Security Controls Summary

| Category | Implementation |
|----------|---------------|
| **Authentication** | Optional API key auth (Bearer token), SHA256 hashed keys, 4 scopes (read:all, write:observations, write:all, admin) |
| **Authorization** | Scope-based endpoint access, admin scope for maintenance/config, scope hierarchy (write:all implies write:observations) |
| **Network Isolation** | Default bind to 127.0.0.1:19380, auth guard blocks non-localhost without auth enabled |
| **Secret Prevention** | SecretScanner with 13 regex patterns (AWS, GitHub, JWT, private keys, etc.), runs before all writes |
| **Input Validation** | SignalGate rejects low-signal content, entity extraction length/format validation, System.Text.Json strongly-typed deserialization |
| **Data Integrity** | Append-only model with validity intervals, backup SHA256 checksums, soft-delete with audit trail |
| **XSS Prevention** | escHtml() in Web UI for all user content, path traversal protection for embedded file serving |
| **Process Isolation** | Hook execution with UseShellExecute=false, configurable timeout, process tree kill |
| **Quality Monitoring** | QualityService with 8 checks (stale, high-fizzle, conflicts, orphans, tag concentration, type imbalance, low-confidence, missing entities) |
| **CORS** | Wildcard origin (by design for Web UI); relies on Bearer token auth (not cookies) for data protection |

---

## 5. Review History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-04-12 | Steve Hansen | Initial STRIDE analysis covering all 10 implementation phases |

---

## 6. References

- [STRIDE Threat Modeling](https://learn.microsoft.com/en-us/azure/security/develop/threat-modeling-tool-threats) — Microsoft Security
- [OWASP Top 10](https://owasp.org/www-project-top-ten/) — Web Application Security Risks
- [RavenDB Security](https://ravendb.net/docs/article-page/7.0/csharp/server/security/overview) — RavenDB Documentation
- [MCP Specification](https://modelcontextprotocol.io/) — Model Context Protocol
