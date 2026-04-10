---
title: C# / .NET
parent: Client SDKs
nav_order: 3
---

# C# / .NET SDK

`Eidet.Sdk` — .NET 8+ client using `HttpClient` and `System.Text.Json`.

## Installation

```bash
dotnet add package Eidet.Sdk
```

## Quick Start

```csharp
using Eidet.Sdk;

using var eidet = new EidetClient("http://localhost:19380");

// Store a memory
var result = await eidet.StoreAsync(new StoreRequest
{
    Repo = @"P:\MyProject",
    Content = "The auth module uses JWT with RS256 signing",
    Type = MemoryType.Observation,
    Tags = ["auth", "jwt"],
    Importance = 0.7f,
});
Console.WriteLine($"Stored: {result.Id}");

// Recall memories
var memories = await eidet.RecallAsync(@"P:\MyProject", "authentication",
    limit: 5, type: MemoryType.Insight);
foreach (var m in memories)
    Console.WriteLine($"[{m.Type}] {m.OneLiner ?? m.Content}");

// Get session context
var context = await eidet.GetContextAsync(@"P:\MyProject");
Console.WriteLine(context);
```

## Authentication

```csharp
using var eidet = new EidetClient("http://localhost:19380", apiKey: "eidet_abc123...");
```

## API Reference

### Constructor

```csharp
new EidetClient(string url = "http://localhost:19380", string? apiKey = null)
```

`EidetClient` implements `IDisposable`. Always wrap in `using` or dispose manually.

### Core Methods

```csharp
// Store a memory
Task<StoreResult> StoreAsync(StoreRequest request, CancellationToken ct = default)

// Search memories
Task<List<SearchResult>> RecallAsync(string repo, string query,
    int limit = 10, MemoryType? type = null, CancellationToken ct = default)

// Get L0+L1 context
Task<string> GetContextAsync(string repo, CancellationToken ct = default)

// Get a single memory
Task<MemoryEntry> GetMemoryAsync(string id, CancellationToken ct = default)

// Soft-delete
Task<bool> ForgetAsync(string id, string? reason = null, CancellationToken ct = default)

// Report feedback
Task<bool> FeedbackAsync(string memoryId, bool wasUsed, CancellationToken ct = default)

// Version chain
Task<List<MemoryEntry>> GetHistoryAsync(string id, CancellationToken ct = default)
```

### Browse & Graph

```csharp
// Paginated browse
Task<BrowseResponse> BrowseAsync(string repo, int skip = 0, int take = 50,
    MemoryType? type = null, CancellationToken ct = default)

// Knowledge graph data
Task<GraphData> GetGraphAsync(string repo, int limit = 200, CancellationToken ct = default)

// List all repos
Task<List<string>> GetReposAsync(CancellationToken ct = default)
```

### Operations

```csharp
// Ingest project files
Task<IntakeResult> IntakeAsync(string repo, CancellationToken ct = default)

// Run consolidation
Task<ConsolidateResult> ConsolidateAsync(string repo, CancellationToken ct = default)

// Export as markdown
Task<string> ExportMarkdownAsync(string repo, CancellationToken ct = default)
```

### Health

```csharp
Task<HealthResponse> HealthAsync(CancellationToken ct = default)
Task<StatusResponse> StatusAsync(CancellationToken ct = default)
Task<bool> IsAvailableAsync(CancellationToken ct = default)
```

### Error Handling

```csharp
try
{
    await eidet.StoreAsync(request);
}
catch (EidetException ex)
{
    Console.Error.WriteLine($"HTTP {ex.StatusCode}: {ex.Body}");
}
```

## Types

### Enums

```csharp
public enum MemoryType { Observation, Insight, Procedure, Heuristic }
```

### Request Types

```csharp
public record StoreRequest
{
    public string Repo { get; init; }
    public string Content { get; init; }
    public MemoryType Type { get; init; }
    public List<string>? Tags { get; init; }
    public float? Importance { get; init; }       // 0.0–1.0
    public string? Source { get; init; }
    public string? SessionId { get; init; }
    public string? Supersedes { get; init; }
}
```

### Response Types

```csharp
public record StoreResult
{
    public string? Id { get; init; }
    public string? Error { get; init; }
    public string? DuplicateId { get; init; }
}

public record SearchResult
{
    public string Id { get; init; }
    public string RepoId { get; init; }
    public MemoryType Type { get; init; }
    public string Content { get; init; }
    public string? OneLiner { get; init; }
    public List<string> Tags { get; init; }
    public float Importance { get; init; }
    public float Score { get; init; }
    public DateTime CreatedAt { get; init; }
    public int? AgeDays { get; init; }
    public string? StalenessWarning { get; init; }
}

public record MemoryEntry { /* all fields */ }
public record BrowseResponse { /* repo, skip, take, count, entries */ }
public record GraphData { /* nodes, edges */ }
```

## Examples

### Health Check Before Operations

```csharp
using var eidet = new EidetClient();

if (!await eidet.IsAvailableAsync())
{
    Console.Error.WriteLine("Eidet service is not running");
    return;
}

// Safe to proceed
var context = await eidet.GetContextAsync(repo);
```

### Integration Test Helper

```csharp
public class MemoryTestFixture : IAsyncDisposable
{
    private readonly EidetClient _client = new();
    private readonly List<string> _storedIds = [];

    public async Task<string> StoreTestMemory(string content, MemoryType type = MemoryType.Observation)
    {
        var result = await _client.StoreAsync(new StoreRequest
        {
            Repo = "test-repo",
            Content = content,
            Type = type,
            Source = "test",
        });
        if (result.Id != null) _storedIds.Add(result.Id);
        return result.Id!;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _storedIds)
            await _client.ForgetAsync(id, "test cleanup");
        _client.Dispose();
    }
}
```
