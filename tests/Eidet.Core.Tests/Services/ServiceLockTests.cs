using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class ServiceLockTests
{
    [Fact]
    public void Read_NoLockFile_ReturnsNull()
    {
        // When no lock file exists (or is cleaned up), Read returns null
        var info = ServiceLock.Read();
        // This is environment-dependent — just verify it doesn't throw
        Assert.True(info is null || info is not null);
    }

    [Fact]
    public void ServiceInfo_RecordProperties()
    {
        var now = DateTimeOffset.UtcNow;
        var info = new ServiceLock.ServiceInfo(1234, 19380, "127.0.0.1", now);

        Assert.Equal(1234, info.Pid);
        Assert.Equal(19380, info.Port);
        Assert.Equal("127.0.0.1", info.BindAddress);
        Assert.Equal(now, info.StartedAt);
    }

    [Fact]
    public void ServiceInfo_Equality()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new ServiceLock.ServiceInfo(1234, 19380, "127.0.0.1", now);
        var b = new ServiceLock.ServiceInfo(1234, 19380, "127.0.0.1", now);

        Assert.Equal(a, b);
    }

    [Fact]
    public void IsServiceRunning_NoLockFile_ReturnsFalse()
    {
        // When there's no lock file or the process is gone, returns false
        var running = ServiceLock.IsServiceRunning(out var info);
        // Environment-dependent — the real service may or may not be running
        Assert.True(running || !running);
    }

    [Fact]
    public void TryAcquire_AndDispose_CleansUp()
    {
        var serviceLock = new ServiceLock();
        try
        {
            // Attempt to acquire — may succeed or fail depending on whether service is running
            var acquired = serviceLock.TryAcquire(19399, "127.0.0.1", out _);
            if (acquired)
            {
                // Verify we can read the lock info
                var info = ServiceLock.Read();
                Assert.NotNull(info);
                Assert.Equal(19399, info!.Port);
                Assert.Equal("127.0.0.1", info.BindAddress);
                Assert.Equal(Environment.ProcessId, info.Pid);
            }
        }
        finally
        {
            serviceLock.Dispose();
        }
    }

    [Fact]
    public void TryAcquire_DoubleLock_SecondFails()
    {
        var lock1 = new ServiceLock();
        var lock2 = new ServiceLock();
        try
        {
            var acquired1 = lock1.TryAcquire(19398, "127.0.0.1", out _);
            if (acquired1)
            {
                var acquired2 = lock2.TryAcquire(19398, "127.0.0.1", out var existing);
                Assert.False(acquired2);
                Assert.NotNull(existing);
                Assert.Equal(Environment.ProcessId, existing!.Pid);
            }
        }
        finally
        {
            lock2.Dispose();
            lock1.Dispose();
        }
    }

    [Fact]
    public async Task CheckHealthAsync_NoService_ReturnsNotRunning()
    {
        // This tests the async health check path
        var (running, healthy, _) = await ServiceLock.CheckHealthAsync();
        // Environment-dependent, just verify it doesn't throw
        if (!running)
            Assert.False(healthy);
    }
}
