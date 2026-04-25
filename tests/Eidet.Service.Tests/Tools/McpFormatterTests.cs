using Eidet.Service.Tools;
using Eidet.Service.Tools.Formatters;

namespace Eidet.Service.Tests.Tools;

public class McpFormatterTests
{
    [Fact]
    public void Ok_RendersTextWithoutErrorFlag()
    {
        var result = ToolResult.Ok(payload: null, summary: "Stored: x");
        var mcp = McpFormatter.Format(result);

        Assert.False(mcp.IsError);
        Assert.Single(mcp.Content);
        Assert.Equal("Stored: x", mcp.Content[0].Text);
    }

    [Theory]
    [InlineData(ToolStatus.NotFound, "missing", "missing")]
    [InlineData(ToolStatus.BadRequest, "bad", "bad")]
    [InlineData(ToolStatus.Conflict, "dup", "dup")]
    [InlineData(ToolStatus.Rejected, "blocked: secret", "blocked: secret")]
    [InlineData(ToolStatus.Internal, "boom", "boom")]
    public void NonOk_FlipsErrorAndCarriesText(ToolStatus status, string summary, string expectedText)
    {
        var result = new ToolResult(status, null, summary);
        var mcp = McpFormatter.Format(result);

        Assert.True(mcp.IsError);
        Assert.Equal(expectedText, mcp.Content[0].Text);
    }

    [Fact]
    public void NullSummary_FallsBackToStatusLabel()
    {
        var result = new ToolResult(ToolStatus.Internal, null, null);
        var mcp = McpFormatter.Format(result);

        Assert.True(mcp.IsError);
        Assert.Equal("Internal error", mcp.Content[0].Text);
    }
}
