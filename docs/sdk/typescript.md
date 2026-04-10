---
title: TypeScript
parent: Client SDKs
nav_order: 1
---

# TypeScript SDK

`@eidet/sdk` — zero-dependency TypeScript client using native `fetch`.

## Installation

```bash
npm install @eidet/sdk
```

## Quick Start

```typescript
import { EidetClient } from '@eidet/sdk';

const eidet = new EidetClient('http://localhost:19380');

// Store a memory
const result = await eidet.store({
  repo: '/path/to/project',
  content: 'The auth module uses JWT with RS256 signing',
  type: 'observation',
  tags: ['auth', 'jwt'],
  importance: 0.7,
});
console.log('Stored:', result.id);

// Recall memories
const memories = await eidet.recall('/path/to/project', 'authentication', {
  limit: 5,
  type: 'insight',
});
for (const m of memories) {
  console.log(`[${m.type}] ${m.oneLiner ?? m.content}`);
}

// Get session context
const context = await eidet.context('/path/to/project');
console.log(context);
```

## Authentication

```typescript
const eidet = new EidetClient({
  url: 'http://localhost:19380',
  apiKey: 'eidet_abc123...',
});
```

## API Reference

### Constructor

```typescript
new EidetClient(url: string)
new EidetClient(options: { url: string; apiKey?: string })
```

### Core Methods

```typescript
// Store a memory
await eidet.store({
  repo: string,
  content: string,
  type: 'observation' | 'insight' | 'procedure' | 'heuristic',
  tags?: string[],
  importance?: number,     // 0.0–1.0, default 0.5
  source?: string,
  sessionId?: string,
  supersedes?: string,     // ID of memory to replace
}): Promise<StoreResult>

// Search memories
await eidet.recall(
  repo: string,
  query: string,
  options?: { limit?: number; type?: string }
): Promise<SearchResult[]>

// Get L0+L1 context
await eidet.context(repo: string): Promise<string>

// Get a single memory
await eidet.getMemory(id: string): Promise<MemoryEntry>

// Soft-delete a memory
await eidet.forget(id: string, reason?: string): Promise<boolean>

// Report feedback
await eidet.feedback(memoryId: string, wasUsed: boolean): Promise<boolean>

// Version chain
await eidet.history(id: string): Promise<MemoryEntry[]>
```

### Browse & Graph

```typescript
// Paginated browse
await eidet.browse(repo: string, options?: {
  skip?: number, take?: number, type?: string
}): Promise<BrowseResponse>

// Knowledge graph data
await eidet.graph(repo: string, limit?: number): Promise<GraphData>

// List all repos
await eidet.repos(): Promise<string[]>
```

### Operations

```typescript
// Ingest project files
await eidet.intake(repo: string): Promise<IntakeResult>

// Run consolidation
await eidet.consolidate(repo: string): Promise<ConsolidateResult>

// Run maintenance
await eidet.maintenance(repo: string): Promise<object>

// Export as markdown
await eidet.exportMarkdown(repo: string): Promise<string>
```

### Health

```typescript
// Health check
await eidet.health(): Promise<HealthResponse>

// Detailed status
await eidet.status(): Promise<StatusResponse>
```

### Error Handling

```typescript
import { EidetClient, EidetError } from '@eidet/sdk';

try {
  await eidet.store({ ... });
} catch (err) {
  if (err instanceof EidetError) {
    console.error(`HTTP ${err.status}: ${err.body}`);
  }
}
```

## Types

All response types are fully typed:

```typescript
interface StoreResult {
  id?: string;
  success: boolean;
  reason?: string;
  duplicateId?: string;
}

interface SearchResult {
  id: string;
  repoId: string;
  type: string;
  content: string;
  oneLiner?: string;
  tags: string[];
  importance: number;
  score: number;
  createdAt: string;
  ageDays?: number;
  stalenessWarning?: string;
}

interface MemoryEntry {
  id: string;
  repoId: string;
  type: string;
  content: string;
  summary?: string;
  oneLiner?: string;
  tags: string[];
  entities: string[];
  importance: number;
  confidence: number;
  accessCount: number;
  echoCount: number;
  fizzleCount: number;
  createdAt: string;
  provenance?: string;
  source?: string;
  foresightHint?: string;
}
```
