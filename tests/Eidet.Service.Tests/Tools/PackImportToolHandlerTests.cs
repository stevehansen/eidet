using System.Text.Json;
using Eidet.Service.Tools;
using Eidet.Service.Tools.Handlers;

namespace Eidet.Service.Tests.Tools;

public class PackImportToolHandlerTests
{
    [Fact]
    public async Task PackImport_NullExportService_ReturnsInternal()
    {
        var handler = new PackImportToolHandler(null, null);

        var result = await Invoke(handler, new { path = "doesnt-matter.md" });

        Assert.Equal(ToolStatus.Internal, result.Status);
        Assert.Contains("not available", result.HumanSummary);
    }

    [Fact]
    public async Task PackImport_NullExport_StillReturnsInternalEvenWithoutPath()
    {
        // Null _export short-circuits before path validation — that's by design.
        var handler = new PackImportToolHandler(null, null);

        var result = await Invoke(handler, new { });
        Assert.Equal(ToolStatus.Internal, result.Status);
    }

    private static Task<ToolResult> Invoke(PackImportToolHandler handler, object args) =>
        handler.ExecuteAsync(new ToolRequest(
            "eidet_pack_import",
            "test-repo",
            JsonSerializer.SerializeToElement(args),
            "test",
            CancellationToken.None));
}
