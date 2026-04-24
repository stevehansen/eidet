# Sync Spec: Eidet Remote Sync & Team Collaboration

> **Status**: Future — not part of the MVP. Designed and specified, implementation deferred.
>
> **Scope**: This spec defines how the Eidet local service syncs with remote backends, shares memories across teams, and handles encryption. The local service ([ServiceSpec](ServiceSpec.md)) is always the primary product — everything in this spec is additive.

---

## Design Principles

1. **Local-first**: The local service is fully functional without remote. Remote adds sync, backup, sharing — never removes local capabilities.
2. **Never lose a memory**: Local copy always exists. Remote is a mirror, not the source of truth for personal memories.
3. **Append-only sync**: The existing append-only model with validity intervals is essentially a CRDT. No conflict resolution needed.
4. **Secrets never leave the machine**: Secret scanning gate runs locally before any sync event is produced.
5. **Multiple backend options**: Hosted SaaS, self-hosted, or orchestrator-only (Bitwarden model).
6. **E2E encryption**: Personal memories encrypted before leaving the machine. Team memories use shared key.
7. **Simple onboarding**: `eidet login` is the entire setup for remote. `eidet team join <url>` for teams.

---

## Sync Model: Append-Only Events

Every mutation in the local memory store produces an event. Events are immutable, ordered, and the unit of sync.

### Event Types

```csharp
public abstract class MemoryEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N")[..16];
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string RepoId { get; set; } = "";
    public string DeviceId { get; set; } = "";      // Stable device identifier
    public string UserId { get; set; } = "";         // Remote user ID (if authenticated)
}

public class MemoryStoredEvent : MemoryEvent
{
    public MemoryEntry Entry { get; set; } = null!;  // Full entry (encrypted for personal)
}

public class MemoryForgottenEvent : MemoryEvent
{
    public string MemoryId { get; set; } = "";
    public string? Reason { get; set; }
}

public class MemoryFeedbackEvent : MemoryEvent
{
    public string MemoryId { get; set; } = "";
    public bool Used { get; set; }                   // Echo or fizzle
    public float ImportanceDelta { get; set; }
    public float ConfidenceDelta { get; set; }
}

public class LayerPublishedEvent : MemoryEvent
{
    public string LayerId { get; set; } = "";
    public string LayerName { get; set; } = "";
    public List<MemoryEntry> Entries { get; set; } = [];
    public string? TeamId { get; set; }
}

public class LinkCreatedEvent : MemoryEvent
{
    public string SourceRepoId { get; set; } = "";
    public string TargetRepoId { get; set; } = "";
    public string Relation { get; set; } = "";
}
```

### Why Append-Only = Trivial Sync

Because memories are never mutated (only created or soft-expired via `ValidUntil`):
- Two devices storing different memories → both are correct, just replicate both
- Two devices forgetting the same memory → idempotent
- Feedback events → applied in order, commutative (+0.05 then -0.10 = -0.10 then +0.05)
- No merge conflicts, no last-write-wins dilemma, no vector clocks needed

This is the single most important architectural advantage for sync.

### Local Event Log

Events are written to a local append-only log before being synced:

```
~/.eidet/data/events.log

Format: newline-delimited JSON (NDJSON)
Each line: { "eventId": "...", "type": "MemoryStored", "timestamp": "...", ... }
```

The log serves as:
- **Sync outbox**: Unsynced events drain to remote when connected
- **Audit trail**: Full history of all mutations
- **Offline buffer**: Queue events when remote is unavailable
- **Replay source**: New devices can catch up by replaying events

---

## Remote Backend Options

### Option A: Hosted SaaS

We run it. User signs up, gets a token, done.

```bash
eidet login
# Opens browser → OAuth flow → token saved to ~/.eidet/auth.json
# Sync starts automatically
```

**Backend components**:
- ASP.NET minimal API
- Database (RavenDB Cloud, or Postgres + pgvector, or Azure Cosmos DB)
- Blob storage for encrypted personal memory sync
- Azure SignalR for real-time notifications
- Azure Service Bus for durable event delivery

**Cost model**: Mostly storage. A developer's memory footprint is tiny (~10-50MB text even after years). Generous free tier possible, with paid team features.

**Config**:
```json
{
  "sync": {
    "enabled": true,
    "provider": "hosted",
    "remoteUrl": "https://api.eidet.dev",
    "autoSync": true
  }
}
```

### Option B: Self-Hosted

Same server code, published as a Docker image. The Bitwarden/Vaultwarden model.

```bash
docker run -d \
  -p 8443:443 \
  -v memory-data:/data \
  ghcr.io/memory-dev/memory-server:latest
```

Or a Helm chart for Kubernetes. The self-hosted version is the same binary, configured to be its own auth authority (local accounts, or OIDC/SAML for enterprise).

**Config**:
```json
{
  "sync": {
    "enabled": true,
    "provider": "self-hosted",
    "remoteUrl": "https://memory.internal.company.com",
    "autoSync": true
  }
}
```

### Option C: Orchestrator-Only (No "Server")

No central server with logic. Just a message relay service.

```
Developer A                    Developer B
    │                               │
    │  ┌─────────────────────┐      │
    └──│  Azure SignalR /    │──────┘
       │  Service Bus        │
       │  (relay only)       │
       └─────────────────────┘
```

**How it works**:
1. Each developer's local service connects to a shared message bus
2. When you "publish" a memory to the team, it's encrypted and sent as a message
3. Other team members' local services receive, decrypt, store as shared layer
4. The bus is configuration-only: connection string in settings, no server code to deploy

**Azure Service Bus** (~$10/mo): Durable message queues. Guaranteed delivery. Perfect for "I was offline, catch me up on what the team shared."

**Azure SignalR Service** (~$50/mo): Managed WebSocket relay. Real-time "eidet published" notifications and presence.

Used together: Service Bus for durable event sync, SignalR for real-time notifications.

**Config**:
```json
{
  "sync": {
    "enabled": true,
    "provider": "orchestrator",
    "serviceBusConnection": "Endpoint=sb://myteam.servicebus.windows.net/;...",
    "signalRConnection": "Endpoint=https://myteam.signalr.net/;...",
    "autoSync": true
  }
}
```

**Team setup**:
```bash
# Team lead creates shared resources (one-time)
eidet team create "Backend Team" --provider azure \
  --service-bus "Endpoint=sb://..." \
  --signalr "Endpoint=https://..."

# Generates invite config
eidet team invite --output team-invite.json

# Team members join
eidet team join team-invite.json
# Or: eidet team join https://eidet.dev/join/abc123
```

This is the cheapest and simplest option for teams on Azure. No server to maintain, no Docker to run.

---

## E2E Encryption

### Key Hierarchy

```
User Master Key (derived from passphrase or OS credential store)
  │
  ├── Personal Key (encrypts personal memories before leaving machine)
  │
  └── Team Key(s) (shared with team members, encrypts team layer content)
       │
       └── Wrapped with each team member's Personal Key for distribution
```

### Personal Memories (E2E Encrypted)

```
Local Service                    Remote
    │                               │
    │  MemoryStoredEvent:           │
    │  {                            │
    │    entry: AES-256-GCM(       │
    │      personalKey,             │
    │      plaintext_entry          │
    │    ),                         │
    │    metadata: {                │  ← Metadata is visible to remote
    │      repoId, type, tags,     │    (needed for routing, stats)
    │      timestamp, eventId      │
    │    }                         │
    │  }                            │
    │  ────────────────────────────→│  Stores encrypted blob
    │                               │  Cannot search content
    │                               │  Cannot read memories
    │←────────────────────────────  │  Returns to other devices
    │  Decrypted locally            │
    │                               │
```

The remote NEVER sees personal memory content. It stores encrypted blobs and routes them to the user's other devices.

**Trade-off**: Server-side search is NOT possible for personal memories. All search happens locally. This is acceptable because the local service always has the full dataset.

### Team Memories (Team Key Encrypted)

When a user publishes a memory to a team layer:

```
Local Service                    Remote
    │                               │
    │  LayerPublishedEvent:         │
    │  {                            │
    │    entries: AES-256-GCM(     │
    │      teamKey,                 │
    │      plaintext_entries        │
    │    ),                         │
    │    teamId: "backend-team"     │
    │  }                            │
    │  ────────────────────────────→│  Stores with team key
    │                               │  
    │                               │  Option 1: Full E2E
    │                               │  Server stores encrypted, 
    │                               │  web UI decrypts client-side
    │                               │  
    │                               │  Option 2: Server-visible
    │                               │  Server decrypts for search,
    │                               │  web UI rendering, analytics
    │                               │
```

**Two modes** (team admin decides):
1. **Full E2E**: Server stores encrypted team memories. Web UI decrypts in browser. Server-side search not available, but local search works. Maximum privacy.
2. **Server-visible**: Server has team key, enables server-side search, web UI rendering, cross-team analytics. Like hosted Bitwarden — you trust the server operator.

For most teams, option 2 is fine. Published team memories are architectural insights, conventions, procedures — not secrets. The secret scanning gate already ensures no credentials enter the memory system.

### Key Distribution

When joining a team:
1. Team admin generates invite (contains team ID + temporary auth token)
2. New member's local service authenticates to remote with invite token
3. Remote returns the team key, wrapped (encrypted) with the new member's personal public key
4. New member's local service unwraps and stores the team key in the OS credential store
5. New member can now decrypt team layer content and publish to it

Key rotation: team admin triggers rotation, new key distributed to all members, re-encrypts team layer content.

---

## Sync Flow

### Normal Operation

```
1. Local service produces event → writes to local event log
2. Sync adapter reads unsynced events from log
3. For each event:
   a. Apply secret scanning gate (block if secrets detected)
   b. Encrypt content (personal key or team key)
   c. Send to remote (HTTP POST, Service Bus message, or SignalR)
4. Remote acknowledges receipt
5. Event marked as synced in local log

Incoming (from other devices or team members):
1. Remote pushes event (SSE, WebSocket, or Service Bus subscription)
2. Local service receives event
3. Decrypt content
4. Apply to local RavenDB (idempotent — skip if event already processed)
5. Mark event as applied
```

### Offline Handling

```
Device goes offline:
  → Events continue to be produced locally
  → Queued in local event log (unsynced)
  → All memory operations work normally

Device comes back online:
  → Sync adapter drains outbox (sends unsynced events)
  → Pulls missed events from remote (since last sync timestamp)
  → Applies missed events locally
  → Catches up in seconds (events are small)
```

No data loss. No degraded functionality offline. The local service IS the full product.

### Conflict Resolution

There are (almost) no conflicts because of the append-only model:
- **Two devices store different memories**: Both are valid. Replicate both.
- **Same memory forgotten on two devices**: Idempotent. Apply once.
- **Feedback on same memory from two devices**: Commutative. Order doesn't matter.
- **Maintenance ran on two devices**: Dedup sweep handles any overlap.

The only edge case: if two devices forget the same memory with different reasons. Resolution: keep both reasons (append to ForgetReason).

---

## Team Collaboration Features

### Selective Publishing

Not all memories are shared. Publishing is explicit:

```bash
# Via MCP tool
eidet_store(content="...", type="insight", publish_to="backend-team")

# Via CLI
eidet publish <memory-id> --team "backend-team"

# Via web UI
# Toggle "Share with team" on individual memories
```

**Default**: All memories are personal/local. Sharing is opt-in per memory.

### Team Layers

When a team member publishes, it creates a `LayerPublishedEvent` that all team members receive. Their local services mount it as a shared (read-only) layer:

```
Developer A publishes:
  "We use FluentValidation for all DTOs" (insight, tags: [validation, patterns])
  → Encrypted with team key
  → Sent to remote
  → Remote relays to all team members
  → Each member's local service:
     1. Decrypts
     2. Stores in local shared layer "team:backend-team"
     3. Available in memory_recall results tagged [team:backend-team]
```

### Multiple Team Connections

A developer can belong to multiple teams, each with its own key and layer:

```json
{
  "sync": {
    "teams": [
      {
        "id": "backend-team",
        "name": "Backend Team",
        "teamKeyRef": "keychain:memory-team-backend"
      },
      {
        "id": "frontend-team",
        "name": "Frontend Team",
        "teamKeyRef": "keychain:memory-team-frontend"
      }
    ]
  }
}
```

Different teams can have different projects/repos they're relevant to. Layer applicability still works: if a team's shared layer has `ApplicableRepos` set, it only surfaces in those repos.

### Per-Project Team Connections

Teams can be scoped to specific projects:

```bash
eidet team join invite.json --repos "P:\MyApp" "P:\MyApi"
# This team's shared layer only applies to these repos
```

---

## Account Model

### Registration

```bash
eidet login
# Opens browser → sign up page
# Email + password (or OAuth with GitHub/Google/Microsoft)
# Returns auth token → stored in ~/.eidet/auth.json (encrypted)
```

No credit card for free tier. No email verification required for basic sync (just backup/restore). Team features may require verified email.

### Account Tiers

| Tier | Price | Sync | Teams | Packs | Web UI |
|------|-------|------|-------|---------|--------|
| Free | $0 | 1 device, 5K memories | No | Import only | Read-only |
| Pro | $X/mo | Unlimited devices + memories | Create/join | Full | Full |
| Team | $Y/user/mo | + team management, admin controls | Unlimited | + marketplace | + analytics |

### Upgrade/Downgrade

**Upgrade to remote** (local-only → synced):
1. `eidet login` → authenticates
2. Local event log replays all existing events to remote
3. Remote now has a mirror of all local memories
4. Future events sync in real-time

**Downgrade to local-only** (synced → local-only):
1. `eidet logout` → removes auth token
2. Local memories remain intact (they were always local)
3. Remote copy persists (for future re-enable or data export)
4. No data loss on either side

**Switch between remote providers** (SaaS → self-hosted):
1. Export from old remote (API or bulk export)
2. Import into new remote
3. Update local config with new remote URL
4. Local event log replays to new remote
5. Seamless transition

---

## The Bitwarden Parallel

| Bitwarden | Eidet |
|-----------|----------------|
| Browser extension / desktop / mobile | Claude Code / TerminalHost / CLI / web |
| Local vault (encrypted, always available) | Local RavenDB (always available) |
| Bitwarden Cloud (sync, share, web vault) | Hosted backend (sync, share, web UI) |
| Vaultwarden (self-hosted) | Docker self-hosted option |
| Organizations (shared vaults) | Teams (shared layers) |
| Collections (selective sharing) | Published memories (selective publish) |
| Master password → encryption key | User key → E2E encryption |
| Sends (time-limited sharing) | Packs (.eidet, shareable) |
| Emergency access | Memory export (markdown, portable) |

---

## Remote Backend Architecture (for SaaS / Self-Hosted)

```
┌──────────────────────────────────────────────────┐
│  Memory Server (ASP.NET minimal API)              │
│                                                  │
│  ┌──────────────┐  ┌──────────────────────────┐ │
│  │ Auth         │  │ Event Store              │ │
│  │ OAuth/OIDC   │  │ Append-only event log    │ │
│  │ API keys     │  │ Per-user partitioned     │ │
│  │ Team mgmt    │  │ Per-team partitioned     │ │
│  └──────────────┘  └──────────────────────────┘ │
│                                                  │
│  ┌──────────────┐  ┌──────────────────────────┐ │
│  │ Blob Store   │  │ Real-time                │ │
│  │ Encrypted    │  │ SignalR hub for push     │ │
│  │ memories     │  │ SSE for web UI           │ │
│  └──────────────┘  └──────────────────────────┘ │
│                                                  │
│  ┌──────────────┐  ┌──────────────────────────┐ │
│  │ Team Layer   │  │ Web UI                   │ │
│  │ Index        │  │ SPA (React/Svelte)       │ │
│  │ (searchable  │  │ Graph viz, browser,      │ │
│  │  if mode 2)  │  │ team mgmt, settings      │ │
│  └──────────────┘  └──────────────────────────┘ │
└──────────────────────────────────────────────────┘
```

The server is intentionally thin:
- **Event store**: Receives and relays events. Partitioned by user and team.
- **Blob store**: Stores encrypted memory content. No indexing for personal memories.
- **Team layer index**: Optionally indexes team memories for server-side search (if Full E2E mode is off).
- **Real-time relay**: SignalR hub pushes events to connected clients.
- **Web UI**: Static SPA served from the same process.
- **Auth**: OAuth/OIDC + API key management + team membership.

Most logic stays in the local service. The server is primarily a dumb relay + encrypted store.

---

## Migration from TerminalHost Embedded Memory

For existing TerminalHost users with local RavenDB memories:

```bash
# Automated migration (from TerminalHost Settings → Memory section)
# 1. TerminalHost detects Eidet service is available
# 2. Exports all memories via eidet_pack_export
# 3. Imports into Eidet service via REST API
# 4. Reconfigures itself to use service API
# 5. Old RavenDB data preserved as backup

# Manual migration
eidet import --from-raven "http://localhost:8080" --database "TerminalHostMemory"
```

The migration preserves:
- All memory entries (with full metadata, provenance, links)
- Layer configuration
- Pack mounts
- Cross-repo links
- Event history (reconstructed from memory creation timestamps)

---

## Future Considerations

- **Peer-to-peer sync**: Direct device-to-device sync without any remote, using mDNS discovery on local network.
- **Conflict resolution for shared layers**: If two team members publish contradicting insights, flag for team review.
- **Memory marketplace**: Public packs for popular frameworks/libraries, discoverable and installable via CLI.
- **Webhooks**: Notify external systems when memories are stored/recalled (CI/CD integration).
- **GraphQL API**: Alternative to REST for flexible querying from web UI and custom tools.
