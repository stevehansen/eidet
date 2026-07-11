using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

/// <summary>
/// Boundary tests for the MutationKind hook funnel (#19): which hook events each public
/// <see cref="MemoryService"/> operation fires, in what order, with what stamped context —
/// and which operations (the <c>RunWriteAsync</c> escape hatch) fire none at all. All
/// deterministic via <see cref="RecordingHookRunner"/>; no sleeps or polling.
/// </summary>
public class MemoryServiceHookTests
{
    private static MemoryEntry MakeEntry(string repoId, string id, string content) => new()
    {
        Id = id,
        RepoId = repoId,
        Type = MemoryType.Insight,
        Content = content,
        CreatedAt = DateTime.UtcNow,
        Validity = new Validity { ValidFrom = DateTime.UtcNow },
        IsLatest = true,
        Importance = 0.7f,
    };

    /// <summary>Reads a property off the anonymous-typed <see cref="HookContext.Data"/> payload.</summary>
    private static object? DataProp(HookContext ctx, string name) =>
        ctx.Data?.GetType().GetProperty(name)?.GetValue(ctx.Data);

    [Fact]
    public async Task StoreAsync_fires_PreStore_then_PostStore_with_stamped_contexts()
    {
        var store = new InMemoryEidetStore();
        var runner = new RecordingHookRunner();
        var svc = new MemoryService(store, hooks: runner);

        var stored = await svc.StoreAsync("repo-a",
            "Auth uses JWT RS256 with 10-minute TTL", MemoryType.Insight);
        Assert.True(stored.Success);
        await runner.Drain();

        Assert.Equal(new[] { HookEvent.PreStore, HookEvent.PostStore }, runner.Fired);

        var contexts = runner.FiredContexts;
        Assert.Equal("pre-store", contexts[0].Context.Event);
        Assert.Equal("post-store", contexts[1].Context.Event);
        Assert.Equal("repo-a", contexts[0].Context.Repo);
        // #19 payload contract: pre-store already carries the deterministic id the entry
        // will be stored under (same payload as post-store).
        Assert.Equal(stored.Id, DataProp(contexts[0].Context, "id"));
        Assert.Equal(stored.Id, DataProp(contexts[1].Context, "id"));
    }

    [Fact]
    public async Task ForgetAsync_fires_PreForget_then_PostForget_with_stamped_contexts()
    {
        var store = new InMemoryEidetStore();
        var entry = MakeEntry("repo-a", "memories/repo-a/insight/1", "redis caching with 5-min ttl");
        await store.StoreAsync(entry); // direct write keeps store hooks out of the recording
        var runner = new RecordingHookRunner();
        var svc = new MemoryService(store, hooks: runner);

        var forgotten = await svc.ForgetAsync(entry.Id, reason: "outdated");
        Assert.True(forgotten);
        await runner.Drain();

        Assert.Equal(new[] { HookEvent.PreForget, HookEvent.PostForget }, runner.Fired);

        var contexts = runner.FiredContexts;
        Assert.Equal("pre-forget", contexts[0].Context.Event);
        Assert.Equal("post-forget", contexts[1].Context.Event);
        Assert.Equal(entry.Id, DataProp(contexts[0].Context, "id"));
        Assert.Equal("outdated", DataProp(contexts[0].Context, "reason"));
    }

    [Fact]
    public async Task Vetoing_PreStore_blocks_the_write_and_fires_no_post_hook()
    {
        var store = new InMemoryEidetStore();
        var runner = new RecordingHookRunner
        {
            PreBehavior = evt => evt == HookEvent.PreStore
                ? HookResult.Rejected("policy says no")
                : HookResult.Ok(),
        };
        var svc = new MemoryService(store, hooks: runner);

        var result = await svc.StoreAsync("repo-a",
            "Auth uses JWT RS256 with 10-minute TTL", MemoryType.Insight);
        await runner.Drain();

        Assert.False(result.Success);
        Assert.Null(result.Id);
        Assert.Contains("Hook rejected", result.Reason);
        Assert.Contains("policy says no", result.Reason);

        // Only the pre-hook fired — the body never ran, so no post-store notification.
        Assert.Equal(new[] { HookEvent.PreStore }, runner.Fired);

        // The entry the pre-hook context named was never written.
        var vetoedId = Assert.IsType<string>(DataProp(runner.FiredContexts[0].Context, "id"));
        Assert.Null(await store.GetAsync(vetoedId));
    }

    [Fact]
    public async Task Vetoing_PreRecall_returns_empty_and_fires_no_PostRecall()
    {
        var store = new InMemoryEidetStore();
        await store.StoreAsync(MakeEntry("repo-a", "memories/repo-a/insight/1", "deployment uses kubernetes"));
        var runner = new RecordingHookRunner
        {
            PreBehavior = evt => evt == HookEvent.PreRecall
                ? HookResult.Rejected("recall gated")
                : HookResult.Ok(),
        };
        var svc = new MemoryService(store, hooks: runner);

        var results = await svc.RecallAsync("repo-a", "deployment");
        await runner.Drain();

        Assert.Empty(results);
        Assert.Equal(new[] { HookEvent.PreRecall }, runner.Fired);
    }

    // ─── RunWriteAsync escape hatch: hookless writes ────────────────

    [Fact]
    public async Task FeedbackAsync_fires_no_hook_events()
    {
        var store = new InMemoryEidetStore();
        var entry = MakeEntry("repo-a", "memories/repo-a/insight/1", "redis caching layer");
        await store.StoreAsync(entry);
        var runner = new RecordingHookRunner();
        var svc = new MemoryService(store, hooks: runner);

        Assert.True(await svc.FeedbackAsync(entry.Id, wasUsed: true));
        await runner.Drain();

        Assert.Empty(runner.Fired);
    }

    [Fact]
    public async Task EditAsync_metadata_only_fires_no_hook_events()
    {
        var store = new InMemoryEidetStore();
        var entry = MakeEntry("repo-a", "memories/repo-a/insight/1", "redis caching layer");
        await store.StoreAsync(entry);
        var runner = new RecordingHookRunner();
        var svc = new MemoryService(store, hooks: runner);

        // Metadata-only edit (Content == null) — updates in place, no supersession chain.
        Assert.Equal(EditOutcome.Updated, await svc.EditAsync(entry.Id, new EditOptions { Importance = 0.9f }));
        await runner.Drain();

        Assert.Empty(runner.Fired);
    }

    [Fact]
    public async Task AddLinkAsync_fires_no_hook_events()
    {
        var store = new InMemoryEidetStore();
        var entry = MakeEntry("repo-a", "memories/repo-a/insight/1", "redis caching layer");
        await store.StoreAsync(entry);
        var runner = new RecordingHookRunner();
        var svc = new MemoryService(store, hooks: runner);

        Assert.True(await svc.AddLinkAsync(entry.Id, "repo-b", "related"));
        await runner.Drain();

        Assert.Empty(runner.Fired);
    }

    // ─── MutationKind pairing + HooksConfig.AnyEnabled ───────────────

    [Fact]
    public void MutationKind_pairs_each_kind_with_its_pre_and_post_events()
    {
        Assert.Equal(HookEvent.PreStore, MutationKind.Store.Pre());
        Assert.Equal(HookEvent.PostStore, MutationKind.Store.Post());
        Assert.Equal(HookEvent.PreForget, MutationKind.Forget.Pre());
        Assert.Equal(HookEvent.PostForget, MutationKind.Forget.Post());
    }

    [Fact]
    public void HooksConfig_AnyEnabled_requires_at_least_one_enabled_hook()
    {
        Assert.False(new HooksConfig().AnyEnabled());

        var allDisabled = new HooksConfig
        {
            PreStore = [new HookDefinition { Command = "check", Enabled = false }],
            PostForget = [new HookDefinition { Command = "notify", Enabled = false }],
        };
        Assert.False(allDisabled.AnyEnabled());

        var oneEnabled = new HooksConfig
        {
            PostRecall = [new HookDefinition { Command = "log" }], // Enabled defaults to true
        };
        Assert.True(oneEnabled.AnyEnabled());
    }
}

/// <summary>
/// Records (event, context) pairs synchronously inside its own <c>Run*HooksAsync</c> — the
/// production fire-and-forget discard (<c>_ = RunPostHooksAsync(...)</c>) happens in a
/// <c>finally</c> that completes before the mutation's task does, so awaiting the mutation
/// then <see cref="Drain"/> is deterministic (no sleep/poll). <see cref="Drain"/> awaits
/// every task this double returned from <c>RunPostHooksAsync</c> — all already completed
/// today; it guards a future truly-async dispatch.
/// </summary>
internal sealed class RecordingHookRunner : IHookRunner
{
    private readonly object _lock = new();
    private readonly List<(HookEvent Event, HookContext Context)> _fired = [];
    private readonly List<Task> _postTasks = [];

    /// <summary>Optional veto knob — return <see cref="HookResult.Rejected"/> to gate a pre-event.</summary>
    public Func<HookEvent, HookResult>? PreBehavior { get; init; }

    /// <summary>Every pre- and post-event, in firing order.</summary>
    public IReadOnlyList<HookEvent> Fired
    {
        get { lock (_lock) return _fired.Select(f => f.Event).ToList(); }
    }

    /// <summary>Fired events with their contexts, for payload asserts.</summary>
    public IReadOnlyList<(HookEvent Event, HookContext Context)> FiredContexts
    {
        get { lock (_lock) return _fired.ToList(); }
    }

    public Task<HookResult> RunPreHooksAsync(HookEvent evt, HookContext context, CancellationToken ct)
    {
        lock (_lock) _fired.Add((evt, context));
        return Task.FromResult(PreBehavior?.Invoke(evt) ?? HookResult.Ok());
    }

    public Task RunPostHooksAsync(HookEvent evt, HookContext context, CancellationToken ct)
    {
        var task = Task.CompletedTask;
        lock (_lock)
        {
            _fired.Add((evt, context));
            _postTasks.Add(task);
        }
        return task;
    }

    public bool HasHooks(HookEvent evt) => true;

    public Task Drain()
    {
        Task[] pending;
        lock (_lock) pending = [.. _postTasks];
        return Task.WhenAll(pending);
    }
}
