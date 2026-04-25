using System.Text.Json;
using Eidet.Service.Mcp;
using Eidet.Service.Tools;

namespace Eidet.Service.Tests.Tools;

public class ToolDispatcherTests
{
    [Fact]
    public async Task UnknownTool_ReturnsNotFound()
    {
        var dispatcher = new ToolDispatcher([new EchoHandler()]);
        var result = await dispatcher.InvokeAsync(NewRequest("nope"));
        Assert.Equal(ToolStatus.NotFound, result.Status);
        Assert.Contains("Unknown tool", result.HumanSummary);
    }

    [Fact]
    public async Task MissingArgument_MapsToBadRequest()
    {
        var dispatcher = new ToolDispatcher([new ThrowHandler(new MissingToolArgumentException("foo"))]);
        var result = await dispatcher.InvokeAsync(NewRequest("throw"));
        Assert.Equal(ToolStatus.BadRequest, result.Status);
        Assert.Contains("foo", result.HumanSummary);
    }

    [Fact]
    public async Task GenericException_MapsToInternal()
    {
        var dispatcher = new ToolDispatcher([new ThrowHandler(new InvalidOperationException("boom"))]);
        var result = await dispatcher.InvokeAsync(NewRequest("throw"));
        Assert.Equal(ToolStatus.Internal, result.Status);
        Assert.Contains("InvalidOperationException", result.HumanSummary);
        Assert.Contains("boom", result.HumanSummary);
    }

    [Fact]
    public async Task RegisteredTool_RoutesToHandler()
    {
        var dispatcher = new ToolDispatcher([new EchoHandler()]);
        Assert.True(dispatcher.IsRegistered("echo"));
        var result = await dispatcher.InvokeAsync(NewRequest("echo"));
        Assert.Equal(ToolStatus.Ok, result.Status);
        Assert.Equal("ok", result.HumanSummary);
    }

    private static ToolRequest NewRequest(string name) =>
        new(name, "test-repo", JsonDocument.Parse("{}").RootElement, "test", CancellationToken.None);

    private sealed class EchoHandler : IToolHandler
    {
        public string Name => "echo";
        public string UsageOp => "Echo";
        public McpToolDefinition Schema { get; } = new() { Name = "echo" };
        public Task<ToolResult> ExecuteAsync(ToolRequest request) =>
            Task.FromResult(ToolResult.Ok(new { ok = true }, "ok"));
    }

    private sealed class ThrowHandler : IToolHandler
    {
        private readonly Exception _ex;
        public ThrowHandler(Exception ex) => _ex = ex;
        public string Name => "throw";
        public string UsageOp => "Throw";
        public McpToolDefinition Schema { get; } = new() { Name = "throw" };
        public Task<ToolResult> ExecuteAsync(ToolRequest request) => throw _ex;
    }
}
