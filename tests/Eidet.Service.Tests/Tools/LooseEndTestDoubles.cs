using Eidet.Core.LooseEnds;

namespace Eidet.Service.Tests.Tools;

/// <summary>Dictionary-backed <see cref="ILooseEndStore"/> for the Park/Resolve handler tests.</summary>
internal sealed class FakeLooseEndStore : ILooseEndStore
{
    public List<LooseEnd> All { get; } = [];

    public Task<string> StoreAsync(LooseEnd e, CancellationToken ct = default)
    {
        All.Add(e);
        return Task.FromResult(e.Id);
    }

    public Task<LooseEnd?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(All.FirstOrDefault(e => e.Id == id));

    public Task UpdateAsync(LooseEnd e, CancellationToken ct = default)
    {
        var idx = All.FindIndex(x => x.Id == e.Id);
        if (idx >= 0) All[idx] = e;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LooseEnd>> ListOpenAsync(string repoId, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LooseEnd>>(All
            .Where(e => e.RepoId == repoId && e.State == LooseEndState.Open)
            .OrderBy(e => e.Priority).ThenBy(e => e.CreatedAt)
            .Take(max).ToList());

    public Task<IReadOnlyList<LooseEnd>> FindOpenByTagsAsync(
        string repoId, IReadOnlyList<string> tags, int max, CancellationToken ct = default)
    {
        if (tags.Count == 0) return Task.FromResult<IReadOnlyList<LooseEnd>>([]);
        var wanted = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<LooseEnd>>(All
            .Where(e => e.RepoId == repoId && e.State == LooseEndState.Open)
            .Where(e => e.Tags.Any(wanted.Contains))
            .OrderBy(e => e.Priority).ThenBy(e => e.CreatedAt)
            .Take(max).ToList());
    }

    public Task<int> CountOpenAsync(string repoId, CancellationToken ct = default) =>
        Task.FromResult(All.Count(e => e.RepoId == repoId && e.State == LooseEndState.Open));
}

/// <summary>Promote port that always succeeds with a fake memory id (handler tests don't exercise the gate).</summary>
internal sealed class FakePromotionPort : IPromotionPort
{
    public Task<PromotionResult> PromoteAsync(LooseEnd e, PromoteOptions opts, CancellationToken ct = default) =>
        Task.FromResult(new PromotionResult(true, "memories/test-repo/insight/abc123", null, null));
}
