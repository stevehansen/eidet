using System.Diagnostics;
using Eidet.Core.Domain;
using Raven.Client.Documents;
using Raven.Client.Documents.Session.TimeSeries;

namespace Eidet.Core.Services;

/// <summary>
/// Tracks API usage statistics per repo using RavenDB time series.
/// Each operation type (store, recall, context, etc.) is recorded as a time series
/// on a per-repo anchor document with values [durationMs, resultCount].
/// </summary>
public class UsageTracker
{
    private readonly IDocumentStore _store;

    public UsageTracker(IDocumentStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Records a usage event for the given repo and operation.
    /// Fire-and-forget safe — swallows all exceptions.
    /// </summary>
    public async Task RecordAsync(string repoId, string operation, double durationMs, int resultCount = 0)
    {
        try
        {
            var docId = RepoUsage.MakeId(repoId);
            using var session = _store.OpenAsyncSession();

            // Ensure anchor document exists
            var existing = await session.LoadAsync<RepoUsage>(docId);
            if (existing is null)
            {
                var usage = new RepoUsage
                {
                    Id = docId,
                    RepoId = RepoIdNormalizer.Normalize(repoId),
                    CreatedAt = DateTime.UtcNow,
                };
                await session.StoreAsync(usage, docId);
            }

            // Append time series entry
            var seriesName = $"Calls/{operation}";
            session.TimeSeriesFor(docId, seriesName)
                .Append(DateTime.UtcNow, [durationMs, resultCount], tag: operation);

            await session.SaveChangesAsync();
        }
        catch
        {
            // Non-critical — never fail the caller
        }
    }

    /// <summary>
    /// Gets aggregated usage stats for a repo within a time range.
    /// Returns per-operation: call count, avg duration, total result count.
    /// </summary>
    public async Task<UsageReport> GetUsageAsync(string repoId, DateTime? from = null, DateTime? to = null)
    {
        var docId = RepoUsage.MakeId(repoId);
        var start = from ?? DateTime.UtcNow.AddDays(-30);
        var end = to ?? DateTime.UtcNow;

        var operations = new[] { "Store", "Recall", "Context", "Forget", "Feedback", "Browse",
            "Graph", "Intake", "Consolidate", "Maintenance", "Export", "History", "Search", "Quality" };

        var report = new UsageReport
        {
            RepoId = RepoIdNormalizer.Normalize(repoId),
            From = start,
            To = end,
        };

        try
        {
            using var session = _store.OpenAsyncSession();
            var doc = await session.LoadAsync<RepoUsage>(docId);
            if (doc is null)
                return report;

            foreach (var op in operations)
            {
                var seriesName = $"Calls/{op}";
                var entries = await session.TimeSeriesFor(docId, seriesName)
                    .GetAsync(start, end);

                if (entries is null || entries.Length == 0) continue;

                var stat = new OperationStats
                {
                    Operation = op,
                    CallCount = entries.Length,
                    TotalDurationMs = entries.Sum(e => e.Values[0]),
                    AvgDurationMs = entries.Average(e => e.Values[0]),
                    MaxDurationMs = entries.Max(e => e.Values[0]),
                    MinDurationMs = entries.Min(e => e.Values[0]),
                    TotalResults = (int)entries.Sum(e => e.Values.Length > 1 ? e.Values[1] : 0),
                    FirstCall = entries.Min(e => e.Timestamp),
                    LastCall = entries.Max(e => e.Timestamp),
                };

                report.Operations.Add(stat);
            }

            report.TotalCalls = report.Operations.Sum(o => o.CallCount);
        }
        catch
        {
            // Return empty report on error
        }

        return report;
    }

    /// <summary>
    /// Gets raw time series data for a specific operation (for charting).
    /// </summary>
    public async Task<List<UsageDataPoint>> GetTimeSeriesAsync(
        string repoId, string operation, DateTime? from = null, DateTime? to = null)
    {
        var docId = RepoUsage.MakeId(repoId);
        var start = from ?? DateTime.UtcNow.AddDays(-30);
        var end = to ?? DateTime.UtcNow;
        var result = new List<UsageDataPoint>();

        try
        {
            using var session = _store.OpenAsyncSession();
            var entries = await session.TimeSeriesFor(docId, $"Calls/{operation}")
                .GetAsync(start, end);

            if (entries is null) return result;

            foreach (var entry in entries)
            {
                result.Add(new UsageDataPoint
                {
                    Timestamp = entry.Timestamp,
                    DurationMs = entry.Values[0],
                    ResultCount = entry.Values.Length > 1 ? (int)entry.Values[1] : 0,
                });
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// Gets hourly aggregated call counts for the last N days (for dashboard chart).
    /// </summary>
    public async Task<List<HourlyBucket>> GetHourlyBreakdownAsync(string repoId, int days = 7)
    {
        var docId = RepoUsage.MakeId(repoId);
        var start = DateTime.UtcNow.AddDays(-days);
        var end = DateTime.UtcNow;
        var buckets = new Dictionary<DateTime, HourlyBucket>();

        var operations = new[] { "Store", "Recall", "Context", "Forget", "Feedback", "Browse",
            "Graph", "Intake", "Consolidate", "Maintenance", "Export", "History", "Search", "Quality" };

        try
        {
            using var session = _store.OpenAsyncSession();
            var doc = await session.LoadAsync<RepoUsage>(docId);
            if (doc is null) return [];

            foreach (var op in operations)
            {
                var entries = await session.TimeSeriesFor(docId, $"Calls/{op}")
                    .GetAsync(start, end);

                if (entries is null) continue;

                foreach (var entry in entries)
                {
                    var hour = new DateTime(entry.Timestamp.Year, entry.Timestamp.Month,
                        entry.Timestamp.Day, entry.Timestamp.Hour, 0, 0, DateTimeKind.Utc);

                    if (!buckets.TryGetValue(hour, out var bucket))
                    {
                        bucket = new HourlyBucket { Hour = hour };
                        buckets[hour] = bucket;
                    }

                    bucket.TotalCalls++;
                    if (!bucket.ByOperation.ContainsKey(op))
                        bucket.ByOperation[op] = 0;
                    bucket.ByOperation[op]++;
                }
            }
        }
        catch { }

        return buckets.Values.OrderBy(b => b.Hour).ToList();
    }

    /// <summary>
    /// Creates a Stopwatch-based scope that records on dispose. Use with `using`.
    /// </summary>
    public UsageScope StartScope(string repoId, string operation) =>
        new(this, repoId, operation);
}

/// <summary>
/// Disposable scope that times an operation and records it on dispose.
/// </summary>
public sealed class UsageScope : IDisposable
{
    private readonly UsageTracker _tracker;
    private readonly string _repoId;
    private readonly string _operation;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private int _resultCount;
    private bool _disposed;

    internal UsageScope(UsageTracker tracker, string repoId, string operation)
    {
        _tracker = tracker;
        _repoId = repoId;
        _operation = operation;
    }

    public void SetResultCount(int count) => _resultCount = count;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sw.Stop();
        // Fire-and-forget
        _ = _tracker.RecordAsync(_repoId, _operation, _sw.Elapsed.TotalMilliseconds, _resultCount);
    }
}

/// <summary>Null implementation for when tracking is disabled.</summary>
public sealed class NullUsageTracker : UsageTracker
{
    public static readonly NullUsageTracker Instance = new();
    private NullUsageTracker() : base(null!) { }

    public new Task RecordAsync(string repoId, string operation, double durationMs, int resultCount = 0) =>
        Task.CompletedTask;

    public new Task<UsageReport> GetUsageAsync(string repoId, DateTime? from = null, DateTime? to = null) =>
        Task.FromResult(new UsageReport { RepoId = repoId, From = from ?? DateTime.UtcNow.AddDays(-30), To = to ?? DateTime.UtcNow });

    public new Task<List<UsageDataPoint>> GetTimeSeriesAsync(string repoId, string operation, DateTime? from = null, DateTime? to = null) =>
        Task.FromResult(new List<UsageDataPoint>());

    public new Task<List<HourlyBucket>> GetHourlyBreakdownAsync(string repoId, int days = 7) =>
        Task.FromResult(new List<HourlyBucket>());

    public new UsageScope StartScope(string repoId, string operation) => new(this, repoId, operation);
}

// ─── Report models ──────────────────────────────────────────────

public class UsageReport
{
    public string RepoId { get; set; } = "";
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public int TotalCalls { get; set; }
    public List<OperationStats> Operations { get; set; } = [];
}

public class OperationStats
{
    public string Operation { get; set; } = "";
    public int CallCount { get; set; }
    public double TotalDurationMs { get; set; }
    public double AvgDurationMs { get; set; }
    public double MaxDurationMs { get; set; }
    public double MinDurationMs { get; set; }
    public int TotalResults { get; set; }
    public DateTime FirstCall { get; set; }
    public DateTime LastCall { get; set; }
}

public class UsageDataPoint
{
    public DateTime Timestamp { get; set; }
    public double DurationMs { get; set; }
    public int ResultCount { get; set; }
}

public class HourlyBucket
{
    public DateTime Hour { get; set; }
    public int TotalCalls { get; set; }
    public Dictionary<string, int> ByOperation { get; set; } = new();
}
