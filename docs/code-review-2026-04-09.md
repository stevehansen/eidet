# Code Review — 2026-04-09

Initial review of the v0.1.0 codebase (65 tests passing, zero warnings).

## Issues

### 1. Triple `DocumentStore` creation in DoctorCommand
**File:** `src/Eidet.Service/Commands/DoctorCommand.cs` (lines 61, 82, 104)
**Severity:** Medium

Each health check (connection, database, index) creates and disposes its own `IDocumentStore`. RavenDB's `DocumentStore` is designed to be a singleton — creating three in sequence is wasteful and opens three separate HTTP connections.

**Fix:** Create one `IDocumentStore` at the start of `ExecuteAsync`, pass it to each check method, dispose once at the end.

---

### 2. `SearchAsync` doesn't filter by validity
**File:** `src/Eidet.Core/Storage/RavenEidetStore.cs` (lines 33-42)
**Severity:** High

Searches return all matching memories regardless of expiry. Expired memories should be excluded from recall per the spec.

**Fix:** Add `.Where(e => e.Validity.ValidUntil == null || e.Validity.ValidUntil > DateTime.UtcNow)` to the query.

---

### 3. `SearchAsync` uses only full-text, not hybrid search
**File:** `src/Eidet.Core/Storage/RavenEidetStore.cs` (line 38)
**Severity:** High

The `Memories_Search` index defines vector search with built-in embeddings, but `SearchAsync` only does `.Search(e => e.Content, query)` (full-text). The spec's core value proposition is hybrid retrieval (vector + full-text + metadata in one round-trip).

**Fix:** Add `VectorSearch()` and merge/rank results alongside full-text hits. Consider a two-query approach (vector + full-text) with score-based deduplication, or a single RQL query that combines both.

---

### 4. `StorageConfig.Mode` is a string, not an enum
**File:** `src/Eidet.Core/Configuration/EidetConfig.cs` (line 20)
**Severity:** Low

`"external"` / `"embedded"` as raw strings invites typos. The doctor command compares with `== "embedded"` — a typo like `"embeded"` would silently fall through.

**Fix:** Create a `StorageMode` enum (`External`, `Embedded`) with `[JsonConverter(typeof(JsonStringEnumConverter))]` for clean JSON serialization.

---

### 5. `Validity.ValidFrom` defaults to `DateTime.UtcNow` in initializer
**File:** `src/Eidet.Core/Domain/Validity.cs` (line 5)
**Severity:** Low

Evaluates at construction time. Fine for runtime, but `new Validity()` in tests or deserialization edge cases gets a non-deterministic timestamp. Also inconsistent with `MemoryEntry.CreatedAt` which has no default.

**Fix:** Default to `default(DateTime)` and set explicitly at the store boundary. Or make `CreatedAt` consistent by also defaulting to `DateTime.UtcNow`.

---

### 6. Index name retrieved via `new Memories_Search().IndexName`
**File:** `src/Eidet.Core/Storage/RavenEidetStore.cs` (line 97)
**Severity:** Low

Allocates the full index definition (including map expressions) just to read the name string.

**Fix:** Add a static property or const: `public const string Name = "Memories/Search";` on the index class.

---

### 7. `[GeneratedRegex]` combined with `RegexOptions.Compiled`
**Files:** `src/Eidet.Core/Gates/SecretScanner.cs`, `src/Eidet.Core/Services/EntityExtractor.cs`
**Severity:** Cosmetic

`[GeneratedRegex]` source-generates native code at compile time. `RegexOptions.Compiled` is a no-op for source-generated regex. Not harmful, but misleading.

**Fix:** Remove `RegexOptions.Compiled` from all `[GeneratedRegex]` attributes.

---

### 8. SecretScanner missing Azure/GCP/Slack patterns
**File:** `src/Eidet.Core/Gates/SecretScanner.cs`
**Severity:** Medium

The 10 patterns cover AWS, GitHub, npm, JWT but miss:
- Azure storage: `DefaultEndpointsProtocol=` / `AccountKey=`
- GCP service account JSON: `"private_key": "-----BEGIN`
- Slack tokens: `xoxb-`, `xoxp-`, `xapp-`
- Generic high-entropy strings (optional, higher false-positive risk)

---

### 9. Version "0.1.0" hardcoded in two places
**Files:** `src/Eidet.Service/Program.cs` (line 9), `src/Eidet.Service/Commands/StatusCommand.cs` (lines 41, 62)
**Severity:** Low

**Fix:** Use a shared constant or pull from `Assembly.GetEntryAssembly().GetName().Version`. When MinVer is set up later this resolves automatically via `<Version>` in the csproj.

---

### 10. `EnvVarRegex` is too broad
**File:** `src/Eidet.Core/Services/EntityExtractor.cs` (line 128)
**Severity:** Low

Pattern `[A-Z][A-Z0-9_]{3,}` matches common English words in ALL CAPS: `IMPORTANT`, `NEVER`, `GOOD`, `NOTE`, `TODO`. This produces false-positive entities from prose content.

**Fix:** Require underscore: `[A-Z][A-Z0-9]*_[A-Z0-9_]{2,}` — matches `NODE_ENV`, `API_KEY` but not `NEVER`.

---

### 11. `Eidet.Service.Tests` has no tests
**File:** `tests/Eidet.Service.Tests/`
**Severity:** Cosmetic

Test runner reports "No test is available." Either add a smoke test or remove until needed.

---

### 12. `EidetPack.Entries` includes runtime counters
**File:** `src/Eidet.Core/Domain/EidetPack.cs`
**Severity:** Low

Exported packs carry `AccessCount`, `EchoCount`, `FizzleCount`, `LastAccessedAt` from the source. When importing, these counters should probably be reset to zero.

**Fix:** Handle in the import logic (not the domain model) — reset counters when importing pack entries.

---

## Not Issues

- **`RepoIdNormalizer` producing leading dashes for Unix paths** — by design, matches Claude Code's normalization scheme.

## Summary

| # | Issue | Severity |
|---|-------|----------|
| 1 | Triple DocumentStore in DoctorCommand | Medium |
| 2 | SearchAsync missing validity filter | High |
| 3 | SearchAsync missing vector search | High |
| 4 | StorageConfig.Mode is string not enum | Low |
| 5 | Validity.ValidFrom non-deterministic default | Low |
| 6 | Index name via full instantiation | Low |
| 7 | Redundant RegexOptions.Compiled | Cosmetic |
| 8 | Missing Azure/GCP/Slack secret patterns | Medium |
| 9 | Hardcoded version string | Low |
| 10 | EnvVarRegex too broad | Low |
| 11 | Empty test project | Cosmetic |
| 12 | Pack export includes runtime counters | Low |
