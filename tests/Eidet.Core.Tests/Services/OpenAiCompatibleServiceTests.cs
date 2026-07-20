using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class OpenAiCompatibleServiceTests
{
    [Fact]
    public void ParseModelIds_LmStudioShape_ReturnsIds()
    {
        var json = """
            {"data":[{"id":"google/gemma-4-12b","object":"model"},{"id":"qwen3-8b","object":"model"}],"object":"list"}
            """;
        var ids = OpenAiCompatibleService.ParseModelIds(json);
        Assert.Equal(["google/gemma-4-12b", "qwen3-8b"], ids);
    }

    [Fact]
    public void ParseModelIds_MissingData_ReturnsEmpty()
    {
        Assert.Empty(OpenAiCompatibleService.ParseModelIds("{}"));
    }

    [Fact]
    public void ParseModelIds_SkipsEntriesWithoutId()
    {
        var json = """
            {"data":[{"object":"model"},{"id":"","object":"model"},{"id":"real-model"}]}
            """;
        Assert.Equal(["real-model"], OpenAiCompatibleService.ParseModelIds(json));
    }

    [Fact]
    public async Task TryListModels_Unreachable_ReturnsNull()
    {
        using var svc = new OpenAiCompatibleService("http://localhost:19999");
        Assert.Null(await svc.TryListModelsAsync());
    }
}
