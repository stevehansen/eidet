using System.Text.Json;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Tools;

public class PackExportToolHandlerTests
{
    [Fact]
    public async Task PackExport_NullExportService_ReturnsInternal()
    {
        var handler = new PackExportToolHandler(null);

        var result = await Invoke(handler, new { pack_id = "demo" });

        Assert.Equal(ToolStatus.Internal, result.Status);
        Assert.Contains("not available", result.HumanSummary);
    }

    private static Task<ToolResult> Invoke(PackExportToolHandler handler, object args) =>
        handler.ExecuteAsync(new ToolRequest(
            "eidet_pack_export",
            "test-repo",
            JsonSerializer.SerializeToElement(args),
            "test",
            CancellationToken.None));
}
