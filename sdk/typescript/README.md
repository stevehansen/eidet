<div align="center">
  <img src="https://raw.githubusercontent.com/stevehansen/eidet/main/logo-512x512.png" alt="Eidet" width="96" height="96">
</div>

# @eidet/sdk

TypeScript client SDK for [Eidet](https://github.com/stevehansen/eidet) — long-term memory for AI coding agents.

## Install

```bash
npm install @eidet/sdk
```

## Usage

```typescript
import { EidetClient } from '@eidet/sdk';

const client = new EidetClient(); // defaults to http://localhost:19380

// Store a memory
await client.store({
  repo: '/path/to/project',
  content: 'The auth module uses JWT with RS256 signing',
  type: 'observation',
  tags: ['auth', 'jwt'],
});

// Search memories
const results = await client.recall('/path/to/project', 'authentication');

// Get session context (L0 identity + L1 top memories, < 600 tokens)
const context = await client.context('/path/to/project');

// Browse all memories
const page = await client.browse('/path/to/project', { skip: 0, take: 50 });

// Feedback loop — mark a memory as useful or irrelevant
await client.feedback('memories/...', true);  // echo (useful)
await client.feedback('memories/...', false); // fizzle (irrelevant)
```

## API Key Authentication

```typescript
const client = new EidetClient({
  url: 'http://localhost:19380',
  apiKey: 'your-api-key',
});
```

## All Methods

| Method | Description |
|--------|-------------|
| `store(request)` | Store a memory (observation, insight, procedure, heuristic; `negative`/`valence` for dead-ends, `stage` for subtask scoping) |
| `recall(repo, query, options?)` | Search memories by meaning and keywords (filters: type, tags, valence, stage, crossRepo) |
| `context(repo)` | Get compact session context (< 600 tokens) |
| `get_memory(id)` | Get a specific memory by ID (includes `contentSha256` for optimistic concurrency) |
| `update(id, changes)` | Update a memory (content changes create versions; `expectedContentSha256` precondition) |
| `redact(id, reason)` | Scrub a memory's content to a tombstone |
| `forget(id, reason?)` | Soft-delete a memory |
| `feedback(memoryId, wasUsed)` | Echo (useful) or fizzle (irrelevant) feedback |
| `history(id)` | Get version chain for a memory |
| `browse(repo, options?)` | Paginated memory listing |
| `graph(repo, limit?)` | Graph data for visualization |
| `repos()` | List all known repositories |
| `intake(repo)` | Ingest project files as seed memories |
| `intakeGit(repo, options?)` | Seed memories from git commit history |
| `intakeClaudeMemory(repo, dryRun?)` | Import Claude Code's native per-project memory |
| `consolidate(repo)` | Merge related observations into insights |
| `maintenance(repo)` | Run maintenance pipeline |
| `exportMarkdown(repo, format?)` | Export memories as markdown (`'agents'` for AGENTS.md shape) |
| `health()` | Health check |
| `status()` | Service status and stats |
| `isAvailable()` | Check if the service is reachable |

## Requirements

- Eidet service running locally (`eidet serve` or installed as system service)
- ESM module (uses native `fetch`)
- Zero runtime dependencies

## License

[MIT](https://github.com/stevehansen/eidet/blob/main/LICENSE)
