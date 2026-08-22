// Eidet TypeScript SDK — REST API client for Eidet memory service

export type MemoryType = 'observation' | 'insight' | 'procedure' | 'heuristic';

export type Valence = 'neutral' | 'affirming' | 'refuting' | 'cautionary';

/** Functional subtask a memory applies to; 'none' means stage-agnostic (matches every stage). */
export type FunctionalStage = 'none' | 'analyze' | 'locate' | 'edit' | 'test' | 'debug' | 'deploy';

export interface StoreRequest {
  repo: string;
  content: string;
  type?: MemoryType;
  tags?: string[];
  importance?: number;
  source?: string;
  sessionId?: string;
  supersedes?: string;
  /** Shorthand for a dead-end: sets valence=refuting, defaults type to heuristic, tags 'dead-end'. */
  negative?: boolean;
  /** Explicit stance toward the subject (overrides negative). */
  valence?: Valence;
  /** Functional subtask this memory applies to; omit for stage-agnostic knowledge. */
  stage?: FunctionalStage;
}

export interface StoreResult {
  id?: string;
  error?: string;
  duplicateId?: string;
  /** True when the store contradicted a high-trust memory: stored but downranked until echoed. */
  quarantined?: boolean;
}

export interface UpdateMemoryRequest {
  /** New content — creates a versioned supersession; metadata-only edits update in place. */
  content?: string;
  tags?: string[];
  importance?: number;
  confidence?: number;
  type?: MemoryType;
  stage?: FunctionalStage;
  oneLiner?: string;
  summary?: string;
  foresightHint?: string;
  /** Optimistic-concurrency precondition: SHA256 of the content you read. Mismatch → 409, no edit. */
  expectedContentSha256?: string;
}

export interface UpdateResult {
  updated: boolean;
  id: string;
  superseded: boolean;
}

export interface MemoryEntry {
  id: string;
  repoId: string;
  type: MemoryType;
  stage?: FunctionalStage;
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
  provenance: string;
  source: string;
  foresightHint?: string;
  /** Present on get_memory only — round-trip as expectedContentSha256 on a subsequent update. */
  contentSha256?: string;
}

export interface SearchResult {
  id: string;
  repoId: string;
  type: MemoryType;
  valence?: Valence;
  stage?: FunctionalStage;
  content: string;
  summary?: string;
  oneLiner?: string;
  tags: string[];
  entities: string[];
  importance: number;
  score: number;
  createdAt: string;
  ageDays?: number;
  stalenessWarning?: string;
}

export interface BrowseResponse {
  repo: string;
  skip: number;
  take: number;
  count: number;
  entries: MemoryEntry[];
}

export interface GraphNode {
  id: string;
  type: MemoryType;
  label: string;
  importance: number;
  tags: string[];
}

export interface GraphEdge {
  from: string;
  to: string;
  relation: string;
}

export interface GraphData {
  nodes: GraphNode[];
  edges: GraphEdge[];
}

export interface HealthResponse {
  status: string;
  version: string;
}

export interface StatusResponse {
  version: string;
  status: string;
  uptime: string;
  api: string;
  database?: {
    name: string;
    serverVersion: string;
    documentCount: number;
    indexExists: boolean;
  };
}

export interface UsageReport {
  repoId: string;
  from: string;
  to: string;
  totalCalls: number;
  operations: OperationStats[];
}

export interface OperationStats {
  operation: string;
  callCount: number;
  totalDurationMs: number;
  avgDurationMs: number;
  maxDurationMs: number;
  minDurationMs: number;
  totalResults: number;
  firstCall: string;
  lastCall: string;
}

export interface UsageDataPoint {
  timestamp: string;
  durationMs: number;
  resultCount: number;
}

export interface HourlyBucket {
  hour: string;
  totalCalls: number;
  byOperation: Record<string, number>;
}

export interface ContextPreview {
  repo: string;
  maxTokens: number;
  context: string;
  estimatedTokens: number;
  layers?: { id: string; name: string; type: string }[];
  crossRepoScope?: string[];
}

export interface EidetClientOptions {
  url?: string;
  apiKey?: string;
}

export class EidetClient {
  private readonly baseUrl: string;
  private readonly apiKey?: string;

  constructor(options: EidetClientOptions | string = {}) {
    if (typeof options === 'string') {
      this.baseUrl = options.replace(/\/$/, '');
    } else {
      this.baseUrl = (options.url ?? 'http://localhost:19380').replace(/\/$/, '');
      this.apiKey = options.apiKey;
    }
  }

  // ─── Core operations ─────────────────────────────────────────────

  async store(request: StoreRequest): Promise<StoreResult> {
    return this.post<StoreResult>('/api/eidet', request);
  }

  async recall(repo: string, query: string, options?: {
    limit?: number;
    type?: MemoryType;
    tags?: string[];
    valence?: Valence;
    stage?: FunctionalStage;
    crossRepo?: boolean;
  }): Promise<SearchResult[]> {
    const params = new URLSearchParams({ repo, q: query });
    if (options?.limit) params.set('limit', String(options.limit));
    if (options?.type) params.set('type', options.type);
    if (options?.tags?.length) params.set('tags', options.tags.join(','));
    if (options?.valence) params.set('valence', options.valence);
    if (options?.stage) params.set('stage', options.stage);
    if (options?.crossRepo) params.set('cross_repo', 'true');
    const data = await this.get<{ results: SearchResult[] }>(`/api/eidet/search?${params}`);
    return data.results;
  }

  async context(repo: string): Promise<string> {
    const data = await this.get<{ context: string }>(`/api/eidet/context?repo=${enc(repo)}`);
    return data.context;
  }

  async get_memory(id: string): Promise<MemoryEntry> {
    return this.get<MemoryEntry>(`/api/eidet/${enc(id)}`);
  }

  /** Update a memory. Content changes create a versioned supersession; a stale
   *  expectedContentSha256 precondition surfaces as an EidetError with status 409. */
  async update(id: string, changes: UpdateMemoryRequest): Promise<UpdateResult> {
    return this.put<UpdateResult>(`/api/eidet/${enc(id)}`, changes);
  }

  /** Scrub a memory's content to a tombstone (audit node preserved). */
  async redact(id: string, reason: string): Promise<boolean> {
    const data = await this.post<{ redacted: boolean }>(`/api/eidet/${enc(id)}/redact`, { reason });
    return data.redacted;
  }

  async forget(id: string, reason?: string): Promise<boolean> {
    const params = reason ? `?reason=${enc(reason)}` : '';
    const data = await this.del<{ forgotten: boolean }>(`/api/eidet/${enc(id)}${params}`);
    return data.forgotten;
  }

  async feedback(memoryId: string, wasUsed: boolean, reason?: string): Promise<boolean> {
    const data = await this.post<{ applied: boolean }>('/api/eidet/feedback', { memoryId, wasUsed, reason });
    return data.applied;
  }

  async history(id: string): Promise<MemoryEntry[]> {
    const data = await this.get<{ chain: MemoryEntry[] }>(`/api/eidet/history/${enc(id)}`);
    return data.chain;
  }

  // ─── Browse & Graph ──────────────────────────────────────────────

  async browse(repo: string, options?: {
    skip?: number;
    take?: number;
    type?: MemoryType;
  }): Promise<BrowseResponse> {
    const params = new URLSearchParams({ repo });
    if (options?.skip != null) params.set('skip', String(options.skip));
    if (options?.take != null) params.set('take', String(options.take));
    if (options?.type) params.set('type', options.type);
    return this.get<BrowseResponse>(`/api/eidet/browse?${params}`);
  }

  async graph(repo: string, limit?: number): Promise<GraphData> {
    const params = new URLSearchParams({ repo });
    if (limit) params.set('limit', String(limit));
    return this.get<GraphData>(`/api/eidet/graph?${params}`);
  }

  async repos(): Promise<string[]> {
    const data = await this.get<{ repos: { repoId: string }[] }>('/api/eidet/repos');
    return data.repos.map(r => r.repoId);
  }

  // ─── Operations ──────────────────────────────────────────────────

  async intake(repo: string): Promise<{ newCount: number; skippedCount: number }> {
    return this.post(`/api/eidet/intake?repo=${enc(repo)}`);
  }

  async intakeGit(
    repo: string,
    options?: { since?: string; maxCommits?: number; allCommits?: boolean; dryRun?: boolean },
  ): Promise<{ newCount: number; skippedCount: number }> {
    let url = `/api/eidet/intake/git?repo=${enc(repo)}`;
    if (options?.since) url += `&since=${enc(options.since)}`;
    if (options?.maxCommits != null) url += `&max_commits=${options.maxCommits}`;
    if (options?.allCommits) url += '&all_commits=true';
    if (options?.dryRun) url += '&dry_run=true';
    return this.post(url);
  }

  /** Import Claude Code's native per-project memory (MEMORY.md) as seed memories. */
  async intakeClaudeMemory(repo: string, dryRun?: boolean): Promise<{ newCount: number; skippedCount: number }> {
    let url = `/api/eidet/intake/claude-memory?repo=${enc(repo)}`;
    if (dryRun) url += '&dry_run=true';
    return this.post(url);
  }

  async consolidate(repo: string): Promise<{ candidates: number; insightsCreated: number; insightsBoosted: number }> {
    return this.post(`/api/eidet/consolidate?repo=${enc(repo)}`);
  }

  /**
   * Run the maintenance pipeline. A pass that outlives the service's grace window is handed back
   * as a run id to poll; this follows it to the end, so the resolved value is always the finished
   * report — a slow repo takes longer, it does not fail.
   */
  async maintenance(repo: string): Promise<Record<string, unknown>> {
    const res = await this.fetch(`/api/maintenance?repo=${enc(repo)}`, { method: 'POST' });
    if (!res.ok) throw new EidetError(res.status, await res.text());

    const body = (await res.json()) as Record<string, unknown>;
    if (res.status !== 202) return body;

    for (;;) {
      await new Promise((done) => setTimeout(done, MAINTENANCE_POLL_MS));
      const run = await this.get<MaintenanceRunStatus>(body.poll as string);
      if (run.status === 'running') continue;
      if (run.status === 'failed') throw new EidetError(500, run.error ?? 'maintenance failed');
      return run.report ?? {};
    }
  }

  /** Render memories as markdown; format 'agents' renders the AGENTS.md interop shape. */
  async exportMarkdown(repo: string, format?: 'agents'): Promise<string> {
    let url = `/api/eidet/export?repo=${enc(repo)}`;
    if (format) url += `&format=${format}`;
    const res = await this.fetch(url);
    return res.text();
  }

  // ─── Usage & Context ──────────────────────────────────────────────

  async usage(repo: string, days?: number): Promise<UsageReport> {
    const params = new URLSearchParams({ repo });
    if (days) params.set('days', String(days));
    return this.get<UsageReport>(`/api/eidet/usage?${params}`);
  }

  async usageTimeSeries(repo: string, operation: string, days?: number): Promise<UsageDataPoint[]> {
    const params = new URLSearchParams({ repo, operation });
    if (days) params.set('days', String(days));
    const data = await this.get<{ data: UsageDataPoint[] }>(`/api/eidet/usage/timeseries?${params}`);
    return data.data;
  }

  async usageHourly(repo: string, days?: number): Promise<HourlyBucket[]> {
    const params = new URLSearchParams({ repo });
    if (days) params.set('days', String(days));
    const data = await this.get<{ buckets: HourlyBucket[] }>(`/api/eidet/usage/hourly?${params}`);
    return data.buckets;
  }

  async contextPreview(repo: string, tokens?: number): Promise<ContextPreview> {
    const params = new URLSearchParams({ repo });
    if (tokens) params.set('tokens', String(tokens));
    return this.get<ContextPreview>(`/api/eidet/context/preview?${params}`);
  }

  // ─── Health ──────────────────────────────────────────────────────

  async health(): Promise<HealthResponse> {
    return this.get<HealthResponse>('/api/health');
  }

  async status(): Promise<StatusResponse> {
    return this.get<StatusResponse>('/api/status');
  }

  async isAvailable(): Promise<boolean> {
    try {
      await this.health();
      return true;
    } catch {
      return false;
    }
  }

  // ─── HTTP helpers ────────────────────────────────────────────────

  private headers(): Record<string, string> {
    const h: Record<string, string> = { 'Content-Type': 'application/json' };
    if (this.apiKey) h['Authorization'] = `Bearer ${this.apiKey}`;
    return h;
  }

  private async fetch(path: string, init?: RequestInit): Promise<Response> {
    const res = await fetch(`${this.baseUrl}${path}`, {
      ...init,
      headers: { ...this.headers(), ...init?.headers },
    });
    return res;
  }

  private async get<T>(path: string): Promise<T> {
    const res = await this.fetch(path);
    if (!res.ok) throw new EidetError(res.status, await res.text());
    return res.json() as Promise<T>;
  }

  private async post<T>(path: string, body?: unknown): Promise<T> {
    const res = await this.fetch(path, {
      method: 'POST',
      body: body ? JSON.stringify(body) : undefined,
    });
    if (!res.ok) throw new EidetError(res.status, await res.text());
    return res.json() as Promise<T>;
  }

  private async put<T>(path: string, body: unknown): Promise<T> {
    const res = await this.fetch(path, {
      method: 'PUT',
      body: JSON.stringify(body),
    });
    if (!res.ok) throw new EidetError(res.status, await res.text());
    return res.json() as Promise<T>;
  }

  private async del<T>(path: string): Promise<T> {
    const res = await this.fetch(path, { method: 'DELETE' });
    if (!res.ok) throw new EidetError(res.status, await res.text());
    return res.json() as Promise<T>;
  }
}

export class EidetError extends Error {
  constructor(public readonly status: number, public readonly body: string) {
    super(`Eidet API error ${status}: ${body}`);
    this.name = 'EidetError';
  }
}

const MAINTENANCE_POLL_MS = 5000;

interface MaintenanceRunStatus {
  status: 'running' | 'completed' | 'failed';
  report?: Record<string, unknown>;
  error?: string;
}

function enc(s: string): string {
  return encodeURIComponent(s);
}
