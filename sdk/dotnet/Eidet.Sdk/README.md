# Eidet.Sdk

C# client SDK for [Eidet](https://github.com/stevehansen/eidet) — long-term memory for AI coding agents.

## Install

```bash
dotnet add package Eidet.Sdk
```

## Usage

```csharp
using Eidet.Sdk;

using var client = new EidetClient(); // defaults to http://localhost:19380

// Store a memory
var result = await client.StoreAsync(new StoreRequest
{
    Repo = "/path/to/project",
    Content = "The auth module uses JWT with RS256 signing",
    Type = "observation",
    Tags = ["auth", "jwt"],
});

// Search memories
var results = await client.RecallAsync("/path/to/project", "authentication");

// Get session context (L0 identity + L1 top memories, < 600 tokens)
var context = await client.ContextAsync("/path/to/project");

// Browse all memories
var page = await client.BrowseAsync("/path/to/project", skip: 0, take: 50);

// Feedback loop
await client.FeedbackAsync("memories/...", wasUsed: true);  // echo (useful)
await client.FeedbackAsync("memories/...", wasUsed: false); // fizzle (irrelevant)
```

## API Key Authentication

```csharp
using var client = new EidetClient("http://localhost:19380", apiKey: "your-api-key");
```

## All Methods

| Method | Description |
|--------|-------------|
| `StoreAsync(request)` | Store a memory (observation, insight, procedure, heuristic) |
| `RecallAsync(repo, query, ...)` | Search memories by meaning and keywords |
| `ContextAsync(repo)` | Get compact session context (< 600 tokens) |
| `GetMemoryAsync(id)` | Get a specific memory by ID |
| `ForgetAsync(id, reason?)` | Soft-delete a memory |
| `FeedbackAsync(memoryId, wasUsed)` | Echo (useful) or fizzle (irrelevant) feedback |
| `HistoryAsync(id)` | Get version chain for a memory |
| `BrowseAsync(repo, ...)` | Paginated memory listing |
| `GraphAsync(repo, limit?)` | Graph data for visualization |
| `ReposAsync()` | List all known repositories |
| `IntakeAsync(repo)` | Ingest project files as seed memories |
| `ConsolidateAsync(repo)` | Merge related observations into insights |
| `MaintenanceAsync(repo)` | Run maintenance pipeline |
| `ExportMarkdownAsync(repo)` | Export memories as markdown |
| `HealthAsync()` | Health check |
| `StatusAsync()` | Service status and stats |
| `IsAvailableAsync()` | Check if service is reachable |

## Requirements

- Eidet service running locally (`eidet serve` or installed as system service)
- .NET 8.0+

## License

[MIT](https://github.com/stevehansen/eidet/blob/main/LICENSE)
