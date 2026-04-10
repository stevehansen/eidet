using System.Reflection;

namespace Eidet.Service.Tests.Api;

public class WebUITests
{
    [Theory]
    [InlineData("Eidet.Service.wwwroot.index.html")]
    [InlineData("Eidet.Service.wwwroot.app.css")]
    [InlineData("Eidet.Service.wwwroot.app.js")]
    public void EmbeddedResource_Exists(string resourceName)
    {
        var assembly = typeof(Eidet.Service.Api.EidetApiServer).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void EmbeddedResources_ContainAllWebUIFiles()
    {
        var assembly = typeof(Eidet.Service.Api.EidetApiServer).Assembly;
        var names = assembly.GetManifestResourceNames();
        var wwwroot = names.Where(n => n.StartsWith("Eidet.Service.wwwroot.")).ToList();
        Assert.True(wwwroot.Count >= 3, $"Expected at least 3 wwwroot resources, found {wwwroot.Count}");
    }

    [Fact]
    public void IndexHtml_ContainsAppStructure()
    {
        var assembly = typeof(Eidet.Service.Api.EidetApiServer).Assembly;
        using var stream = assembly.GetManifestResourceStream("Eidet.Service.wwwroot.index.html")!;
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();

        Assert.Contains("Eidet", html);
        Assert.Contains("Memory Explorer", html);
        Assert.Contains("page-dashboard", html);
        Assert.Contains("page-browser", html);
        Assert.Contains("page-graph", html);
        Assert.Contains("graphCanvas", html);
        Assert.Contains("app.css", html);
        Assert.Contains("app.js", html);
    }

    [Fact]
    public void AppJs_ContainsCoreFunctions()
    {
        var assembly = typeof(Eidet.Service.Api.EidetApiServer).Assembly;
        using var stream = assembly.GetManifestResourceStream("Eidet.Service.wwwroot.app.js")!;
        using var reader = new StreamReader(stream);
        var js = reader.ReadToEnd();

        Assert.Contains("loadDashboard", js);
        Assert.Contains("loadBrowser", js);
        Assert.Contains("loadGraph", js);
        Assert.Contains("loadTimeline", js);
        Assert.Contains("loadSettings", js);
        Assert.Contains("/api/eidet/browse", js);
        Assert.Contains("/api/eidet/graph", js);
    }

    [Fact]
    public void AppCss_ContainsStyles()
    {
        var assembly = typeof(Eidet.Service.Api.EidetApiServer).Assembly;
        using var stream = assembly.GetManifestResourceStream("Eidet.Service.wwwroot.app.css")!;
        using var reader = new StreamReader(stream);
        var css = reader.ReadToEnd();

        Assert.Contains("--bg-primary", css);
        Assert.Contains(".memory-item", css);
        Assert.Contains("#graphCanvas", css);
        Assert.Contains(".timeline-container", css);
    }
}
