using System.Reflection;
using Eidet.Core.Enrichment;

namespace Eidet.Core.Tests.Services;

/// <summary>
/// Guards the recoverability contract of <c>EnrichmentHealthCache.IsAvailable</c>: a stale
/// unhealthy verdict must not pin enrichment off forever, or a transient backend restart
/// would permanently disable enrichment for the process lifetime.
/// </summary>
public class EnrichmentHealthCacheTests
{
    private static EnrichmentHealthCache NewCache() =>
        (EnrichmentHealthCache)Activator.CreateInstance(
            typeof(EnrichmentHealthCache), new HttpClient())!;

    private static void SetState(EnrichmentHealthCache cache, bool? lastHealthy, DateTime lastCheck)
    {
        var t = typeof(EnrichmentHealthCache);
        t.GetField("_lastHealthy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cache, lastHealthy);
        t.GetField("_lastCheck", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cache, lastCheck);
    }

    [Fact]
    public void IsAvailable_StaleUnhealthy_BecomesAvailableForRetry()
    {
        var cache = NewCache();
        SetState(cache, lastHealthy: false, lastCheck: DateTime.UtcNow.AddMinutes(-6));

        // After the cache goes stale, IsAvailable must flip back to true so the next
        // call re-probes and the backend can recover.
        Assert.True(cache.IsAvailable);
    }

    [Fact]
    public void IsAvailable_RecentUnhealthy_StaysUnavailable()
    {
        var cache = NewCache();
        SetState(cache, lastHealthy: false, lastCheck: DateTime.UtcNow);

        // Within the cache window an unhealthy verdict still suppresses calls.
        Assert.False(cache.IsAvailable);
    }

    [Fact]
    public void IsAvailable_Healthy_IsAvailable()
    {
        var cache = NewCache();
        SetState(cache, lastHealthy: true, lastCheck: DateTime.UtcNow);

        Assert.True(cache.IsAvailable);
    }
}
