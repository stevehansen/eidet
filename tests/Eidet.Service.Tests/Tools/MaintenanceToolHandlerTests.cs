using System.Text.Json;
using Eidet.Core.Maintenance;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Tools;

public class MaintenanceToolHandlerTests
{
    [Fact]
    public async Task Maintenance_RunsAndReturnsReport()
    {
        var runner = new RecordingRunner();
        var handler = new MaintenanceToolHandler(runner);

        var result = await Invoke(handler, new { });

        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.NotNull(result.Payload);
        Assert.Equal(1, runner.Calls);
    }

    private static Task<ToolResult> Invoke(MaintenanceToolHandler handler, object args) =>
        handler.ExecuteAsync(new ToolRequest(
            "eidet_maintenance",
            "test-repo",
            JsonSerializer.SerializeToElement(args),
            "test",
            CancellationToken.None));

    private sealed class RecordingRunner : IMaintenanceRunner
    {
        public int Calls;

        public Task<MaintenanceReport> RunAsync(string repoPathOrId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new MaintenanceReport { RepoId = repoPathOrId });
        }

        public Task<MaintenanceReport> RunAsync(MaintenanceRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new MaintenanceReport { RepoId = request.RepoId });
        }
    }
}
