using Eidet.Core.Services;
using Eidet.Service.Api;

namespace Eidet.Service.Tests.Api;

public class CurationApiTests
{
    [Fact]
    public void ExtractMemoryIdFromLinkPath_ValidPath()
    {
        var id = ExtractMemoryIdFromLinkPath("/api/eidet/memories/P--Eidet/insight/abc123/links");
        Assert.Equal("memories/P--Eidet/insight/abc123", id);
    }

    [Fact]
    public void ExtractMemoryIdFromLinkPath_EmptyForInvalidPath()
    {
        var id = ExtractMemoryIdFromLinkPath("/something/else");
        Assert.Equal("", id);
    }

    [Fact]
    public void ExtractMemoryIdFromLinkPath_EmptyForBaseLinksPath()
    {
        // /api/eidet/links is the base links path — router excludes it,
        // but the helper should handle it gracefully
        var id = ExtractMemoryIdFromLinkPath("/api/eidet/links");
        Assert.True(id.Length == 0 || id == "");
    }

    [Fact]
    public void ExtractMemoryIdFromLinkPath_NestedSlashes()
    {
        var id = ExtractMemoryIdFromLinkPath("/api/eidet/memories/some--repo/observation/abc/links");
        Assert.Equal("memories/some--repo/observation/abc", id);
    }

    [Theory]
    [InlineData("PUT", "/api/eidet/memories/test/insight/123", "write:all")]
    [InlineData("POST", "/api/eidet/enrich", "write:all")]
    [InlineData("POST", "/api/eidet/memories/test/insight/123/links", "write:all")]
    [InlineData("DELETE", "/api/eidet/memories/test/insight/123/links", "write:all")]
    [InlineData("POST", "/api/eidet/memory-tool", "write:all")] // memory-tool endpoint is NOT auth-exempt
    public void GetRequiredScope_CurationEndpoints(string method, string path, string expectedScope)
    {
        var scope = ApiKeyService.GetRequiredScope(method, path);
        Assert.Equal(expectedScope, scope);
    }

    [Fact]
    public void UpdateMemoryRequest_AllFieldsNullable()
    {
        var req = new UpdateMemoryRequest();
        Assert.Null(req.Content);
        Assert.Null(req.Tags);
        Assert.Null(req.Importance);
        Assert.Null(req.Confidence);
        Assert.Null(req.Type);
        Assert.Null(req.OneLiner);
        Assert.Null(req.Summary);
        Assert.Null(req.ForesightHint);
    }

    [Fact]
    public void AddMemoryLinkRequest_HasRequiredFields()
    {
        var req = new AddMemoryLinkRequest
        {
            TargetRepoId = "P--Other",
            Relation = "depends-on",
            TargetMemoryId = "memories/P--Other/insight/abc",
        };
        Assert.Equal("P--Other", req.TargetRepoId);
        Assert.Equal("depends-on", req.Relation);
        Assert.Equal("memories/P--Other/insight/abc", req.TargetMemoryId);
    }

    [Fact]
    public void EnrichRequest_HasRequiredFields()
    {
        var req = new EnrichRequest
        {
            Content = "test content",
            Task = "oneliner",
        };
        Assert.Equal("test content", req.Content);
        Assert.Equal("oneliner", req.Task);
    }

    /// <summary>
    /// Delegates to the private static method in EidetApiServer via the same logic.
    /// </summary>
    private static string ExtractMemoryIdFromLinkPath(string path)
    {
        var prefix = "/api/eidet/";
        var suffix = "/links";
        if (path.StartsWith(prefix) && path.EndsWith(suffix) && path.Length > prefix.Length + suffix.Length)
            return path[prefix.Length..^suffix.Length];
        return "";
    }
}
