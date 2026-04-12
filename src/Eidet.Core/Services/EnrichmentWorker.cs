using Eidet.Core.Domain;
using Raven.Client.Documents;
using Raven.Client.Documents.Subscriptions;

namespace Eidet.Core.Services;

/// <summary>
/// Background worker that enriches new memories via RavenDB data subscription.
/// Triggers immediately when a memory is stored (no waiting for maintenance).
/// Non-blocking — runs as a background task with retry on failure.
/// </summary>
public sealed class EnrichmentWorker : IDisposable
{
    internal const string SubscriptionName = "enrichment-worker";

    private readonly IDocumentStore _store;
    private readonly IEnrichmentService _enrichment;
    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public EnrichmentWorker(IDocumentStore store, IEnrichmentService enrichment)
    {
        _store = store;
        _enrichment = enrichment;
    }

    /// <summary>
    /// Creates the subscription (idempotent) and starts the background worker.
    /// Does nothing if enrichment is not available (NullEnrichmentService).
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (_enrichment is NullEnrichmentService) return;

        await EnsureSubscriptionAsync(ct);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _workerTask = Task.Run(() => RunWorkerLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task EnsureSubscriptionAsync(CancellationToken ct)
    {
        // Check if subscription already exists
        var existing = _store.Subscriptions.GetSubscriptions(0, 100);
        if (existing.Any(s => s.SubscriptionName == SubscriptionName))
            return;

        await _store.Subscriptions.CreateAsync(new SubscriptionCreationOptions
        {
            Name = SubscriptionName,
            Query = "from 'MemoryEntries' where Summary = null and Validity.ValidUntil = null",
        }, token: ct);
    }

    private async Task RunWorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var worker = _store.Subscriptions.GetSubscriptionWorker<MemoryEntry>(
                    new SubscriptionWorkerOptions(SubscriptionName)
                    {
                        MaxDocsPerBatch = 5,
                        Strategy = SubscriptionOpeningStrategy.TakeOver,
                    });

                await worker.Run(async batch =>
                {
                    foreach (var item in batch.Items)
                    {
                        if (ct.IsCancellationRequested) break;
                        await EnrichEntryAsync(item.Result, ct);
                    }
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Connection lost or subscription error — wait and retry
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task EnrichEntryAsync(MemoryEntry entry, CancellationToken ct)
    {
        if (!_enrichment.IsAvailable) return;
        if (string.IsNullOrWhiteSpace(entry.Content)) return;

        var changed = false;

        // Summary (1-2 sentences)
        if (string.IsNullOrEmpty(entry.Summary))
        {
            var summary = await _enrichment.GenerateSummaryAsync(entry.Content, ct);
            if (!string.IsNullOrEmpty(summary))
            {
                entry.Summary = summary;
                changed = true;
            }
        }

        // OneLiner upgrade (replace heuristic with LLM)
        if (entry.OneLiner == EntityExtractor.GenerateHeuristicOneLiner(entry.Content))
        {
            var oneLiner = await _enrichment.GenerateOneLinerAsync(entry.Content, ct);
            if (!string.IsNullOrEmpty(oneLiner))
            {
                entry.OneLiner = oneLiner;
                changed = true;
            }
        }

        // ForesightHint
        if (string.IsNullOrEmpty(entry.ForesightHint))
        {
            var hint = await _enrichment.GenerateForesightHintAsync(entry.Content, ct);
            if (!string.IsNullOrEmpty(hint))
            {
                entry.ForesightHint = hint;
                changed = true;
            }
        }

        // LLM entity extraction (supplement regex)
        if (entry.Entities.Count < 2)
        {
            var llmEntities = await _enrichment.ExtractEntitiesAsync(entry.Content, ct);
            if (llmEntities.Count > 0)
            {
                var existing = new HashSet<string>(entry.Entities, StringComparer.OrdinalIgnoreCase);
                foreach (var e in llmEntities)
                {
                    if (existing.Add(e))
                        entry.Entities.Add(e);
                }
                changed = true;
            }
        }

        if (changed)
        {
            using var session = _store.OpenAsyncSession();
            await session.StoreAsync(entry, entry.Id, ct);
            await session.SaveChangesAsync(ct);
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        // Give the worker a moment to finish gracefully
        _workerTask?.Wait(TimeSpan.FromSeconds(5));
        _cts?.Dispose();
    }
}
