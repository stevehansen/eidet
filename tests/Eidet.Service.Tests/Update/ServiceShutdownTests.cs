using Eidet.Service.Update;

namespace Eidet.Service.Tests.Update;

/// <summary>
/// The updater asks <c>eidet serve</c> to exit on its own before falling back to <c>schtasks /end</c>,
/// so a service-spawned unattended install never depends on how Task Scheduler ends a running task.
/// </summary>
public class ServiceShutdownTests
{
    [Fact]
    public void Signal_ReachesTheListeningProcess()
    {
        if (!OperatingSystem.IsWindows()) return; // SIGTERM is the stop signal elsewhere

        using var requested = new ManualResetEventSlim();
        using (ServiceShutdown.Listen(requested.Set))
        {
            Assert.True(ServiceShutdown.Signal(Environment.ProcessId));
            Assert.True(requested.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void NobodyListening_FallsBackToTheServiceManager()
    {
        // An older service (or none) never created the event; the caller must then use /end.
        Assert.False(ServiceShutdown.Signal(Environment.ProcessId));
        Assert.False(ServiceShutdown.RequestStop(Environment.ProcessId, TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void AfterDispose_TheServiceNoLongerListens()
    {
        if (!OperatingSystem.IsWindows()) return;

        ServiceShutdown.Listen(() => { }).Dispose();

        Assert.False(ServiceShutdown.Signal(Environment.ProcessId));
    }
}
