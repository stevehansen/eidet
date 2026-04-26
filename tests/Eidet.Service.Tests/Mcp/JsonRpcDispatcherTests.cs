using System.Text.Json;
using Eidet.Service.Mcp;

namespace Eidet.Service.Tests.Mcp;

public class JsonRpcDispatcherTests
{
    [Fact]
    public async Task Dispatch_ParseError_ReturnsCode32700()
    {
        var d = NewDispatcher();
        var resp = await d.DispatchAsync("not json", CancellationToken.None);

        Assert.NotNull(resp);
        Assert.NotNull(resp!.Error);
        Assert.Equal(-32700, resp.Error!.Code);
        Assert.Null(resp.Id);
    }

    [Fact]
    public async Task Dispatch_MissingMethod_ReturnsCode32600()
    {
        var d = NewDispatcher();
        var resp = await d.DispatchAsync("""{"jsonrpc":"2.0","id":1}""", CancellationToken.None);

        Assert.NotNull(resp);
        Assert.NotNull(resp!.Error);
        Assert.Equal(-32600, resp.Error!.Code);
    }

    [Fact]
    public async Task Dispatch_UnknownMethod_ReturnsCode32601_AndPreservesId()
    {
        var d = NewDispatcher();
        var resp = await d.DispatchAsync("""{"jsonrpc":"2.0","id":42,"method":"nope"}""", CancellationToken.None);

        Assert.NotNull(resp);
        Assert.NotNull(resp!.Error);
        Assert.Equal(-32601, resp.Error!.Code);
        Assert.Contains("nope", resp.Error.Message);
        Assert.NotNull(resp.Id);
        Assert.Equal(42, resp.Id!.Value.GetInt32());
    }

    [Fact]
    public async Task Dispatch_RegisteredHandler_ReceivesRequestAndReturnsResponse()
    {
        JsonRpcRequest? captured = null;
        var d = new JsonRpcDispatcher(new Dictionary<string, JsonRpcDispatcher.Handler>
        {
            ["echo"] = (req, _) =>
            {
                captured = req;
                return Task.FromResult<JsonRpcResponse?>(JsonRpcResponse.Success(req.Id, new { ok = true }));
            },
        });

        var resp = await d.DispatchAsync("""{"jsonrpc":"2.0","id":"abc","method":"echo","params":{"x":1}}""", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("echo", captured!.Method);
        Assert.NotNull(resp);
        Assert.Null(resp!.Error);
        Assert.NotNull(resp.Id);
        Assert.Equal("abc", resp.Id!.Value.GetString());
    }

    [Fact]
    public async Task Dispatch_NotificationHandler_ReturnsNullResponse()
    {
        var d = new JsonRpcDispatcher(new Dictionary<string, JsonRpcDispatcher.Handler>
        {
            ["notifications/initialized"] = (_, _) => Task.FromResult<JsonRpcResponse?>(null),
        });

        var resp = await d.DispatchAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", CancellationToken.None);

        Assert.Null(resp);
    }

    [Fact]
    public void SerializerOptions_UsesCamelCase()
    {
        var json = JsonSerializer.Serialize(new JsonRpcResponse { Id = null, Result = new { CamelHump = 1 } },
            JsonRpcDispatcher.SerializerOptions);

        Assert.Contains("camelHump", json);
        Assert.DoesNotContain("CamelHump", json);
    }

    private static JsonRpcDispatcher NewDispatcher() =>
        new(new Dictionary<string, JsonRpcDispatcher.Handler>());
}
