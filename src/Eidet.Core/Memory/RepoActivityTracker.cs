using System.Collections.Concurrent;

namespace Eidet.Core.Memory;

/// <summary>
/// Tracks last-touched timestamps per normalized repo id. Used to gate repo-scoped
/// background work on whether the repo has been active recently.
/// </summary>
internal sealed class RepoActivityTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _lastActive = new();

    public void Track(string repoId) => _lastActive[repoId] = DateTime.UtcNow;

    public bool IsActive(string repoId, int withinDays)
    {
        if (_lastActive.TryGetValue(repoId, out var lastActive))
            return (DateTime.UtcNow - lastActive).TotalDays <= withinDays;
        return false;
    }
}
