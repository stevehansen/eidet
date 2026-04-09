# Code Review — 2026-04-09

Initial review of the v0.1.0 codebase. Updated after fixes were applied.

## Resolved (9/12)

| # | Issue | Resolution |
|---|-------|-----------|
| 1 | Triple DocumentStore in DoctorCommand | Single store shared across checks |
| 2 | SearchAsync missing validity filter | `ApplyFilters` excludes expired unless `IncludeExpired` |
| 3 | SearchAsync missing vector search | Separate `VectorSearchAsync` + `FullTextSearchAsync` methods |
| 4 | StorageConfig.Mode was string | `StorageMode` enum with `JsonStringEnumConverter` |
| 6 | Index name via full instantiation | `Memories_Search.IndexName_` const |
| 7 | Redundant RegexOptions.Compiled | Removed from all `[GeneratedRegex]` |
| 9 | Hardcoded version string | `EidetVersion.Current` const |
| 10 | EnvVarRegex too broad | Now requires underscore: `[A-Z][A-Z0-9]*_[A-Z0-9_]{2,}` |
| 3+ | No hybrid merge/rank | Full-text and vector are now separate methods — caller decides how to combine |

## Remaining (3/12)

### 5. `Validity.ValidFrom` defaults to `DateTime.UtcNow` in initializer
**File:** `src/Eidet.Core/Domain/Validity.cs` (line 5)
**Severity:** Low

Still evaluates at construction time. `StoreAsync` now sets `CreatedAt` if default, which partially addresses consistency, but `Validity.ValidFrom` still gets a non-deterministic timestamp on `new Validity()`.

---

### 8. SecretScanner missing Azure/GCP/Slack patterns
**File:** `src/Eidet.Core/Gates/SecretScanner.cs`
**Severity:** Medium

Still 10 original patterns. Missing:
- Azure storage: `DefaultEndpointsProtocol=` / `AccountKey=`
- GCP service account JSON: `"private_key": "-----BEGIN`
- Slack tokens: `xoxb-`, `xoxp-`, `xapp-`

---

### 11. `Eidet.Service.Tests` has no tests
**File:** `tests/Eidet.Service.Tests/`
**Severity:** Cosmetic

Still empty. Test runner reports "No test is available."

---

## New Since Review

Good additions since the initial code:
- `MemoryQuery` + `MemorySearchResult` domain classes
- `Memories_CountByType` map-reduce index (only counts valid memories)
- `IEidetStore` expanded: `ForgetAsync`, `UpdateAsync`, `FindDuplicateAsync` (vector + full-text fallback), `GetTopScoredAsync`, `GetCountsByTypeAsync`
- `EidetVersion.Current` const
- `ServeCommand` + REST API scaffolding
- Tests: 65 → 71, all passing, zero warnings
