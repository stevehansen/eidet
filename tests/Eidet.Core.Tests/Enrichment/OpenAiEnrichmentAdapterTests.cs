using System.Text.Json;
using Eidet.Core.Enrichment;

namespace Eidet.Core.Tests.Enrichment;

/// <summary>
/// Pins the request body: what always goes on the wire, and that the thinking kwarg is present
/// only when configured — a strict gateway rejects fields it does not know, so "unset" must
/// mean absent, not <c>null</c>.
/// </summary>
public class OpenAiEnrichmentAdapterTests
{
    private static JsonElement Payload(bool? thinking) =>
        JsonDocument.Parse(OpenAiEnrichmentAdapter.BuildPayload("deepseek-v4-flash", "Summarize this.", thinking)).RootElement;

    [Fact]
    public void BuildPayload_AlwaysCarriesModelMessagesAndNoStream()
    {
        var payload = Payload(thinking: null);

        Assert.Equal("deepseek-v4-flash", payload.GetProperty("model").GetString());
        Assert.False(payload.GetProperty("stream").GetBoolean());
        var message = Assert.Single(payload.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("Summarize this.", message.GetProperty("content").GetString());
    }

    [Fact]
    public void BuildPayload_ThinkingUnset_SendsNoTemplateKwargs()
    {
        Assert.False(Payload(thinking: null).TryGetProperty("chat_template_kwargs", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildPayload_ThinkingSet_RidesAsChatTemplateKwarg(bool thinking)
    {
        var kwargs = Payload(thinking).GetProperty("chat_template_kwargs");
        Assert.Equal(thinking, kwargs.GetProperty("thinking").GetBoolean());
    }

    [Fact]
    public void ModelName_IsTheConfiguredModel()
    {
        using var adapter = new OpenAiEnrichmentAdapter("http://localhost:19999", "qwen");
        Assert.Equal("qwen", adapter.ModelName);
    }
}
