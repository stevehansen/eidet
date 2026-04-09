using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class EntityExtractorTests
{
    [Fact]
    public void Extract_FindsPascalCaseIdentifiers()
    {
        var entities = EntityExtractor.Extract("The TerminalHost uses MemoryService for storage");
        Assert.Contains("TerminalHost", entities);
        Assert.Contains("MemoryService", entities);
    }

    [Fact]
    public void Extract_FindsBacktickCode()
    {
        var entities = EntityExtractor.Extract("Call `CreateVector()` to generate embeddings with `IndexCreation`");
        // Parens get trimmed by AddEntity, so it becomes "CreateVector"
        Assert.Contains(entities, e => e.Contains("CreateVector"));
    }

    [Fact]
    public void Extract_FindsApiEndpoints()
    {
        var entities = EntityExtractor.Extract("Use POST /api/memory to store a new entry via the API");
        Assert.Contains(entities, e => e.Contains("/api/memory"));
    }

    [Fact]
    public void Extract_FindsCliCommands()
    {
        var entities = EntityExtractor.Extract("Run dotnet build to compile the solution then git status");
        Assert.Contains(entities, e => e.Contains("dotnet build"));
    }

    [Fact]
    public void Extract_FindsDottedIdentifiers()
    {
        var entities = EntityExtractor.Extract("Use Microsoft.Extensions.Hosting for service hosting");
        Assert.Contains(entities, e => e.Contains("Microsoft.Extensions.Hosting"));
    }

    [Fact]
    public void Extract_FindsErrorCodes()
    {
        var entities = EntityExtractor.Extract("Fix the CS8602 null reference warning and HTTP 404 error");
        Assert.Contains("CS8602", entities);
        Assert.Contains("HTTP 404", entities);
    }

    [Fact]
    public void Extract_FindsEnvironmentVariables()
    {
        var entities = EntityExtractor.Extract("Set ASPNETCORE_ENVIRONMENT to Development and check NODE_ENV");
        Assert.Contains("ASPNETCORE_ENVIRONMENT", entities);
        Assert.Contains("NODE_ENV", entities);
    }

    [Fact]
    public void Extract_DeduplicatesResults()
    {
        var entities = EntityExtractor.Extract("TerminalHost connects to TerminalHost service via TerminalHost API");
        Assert.Single(entities, e => e == "TerminalHost");
    }

    [Fact]
    public void Extract_ReturnsEmptyForBlankContent()
    {
        Assert.Empty(EntityExtractor.Extract(""));
        Assert.Empty(EntityExtractor.Extract("   "));
    }

    [Fact]
    public void Extract_LimitsTo20Entities()
    {
        // Generate content with many identifiable entities
        var content = string.Join(" ", Enumerable.Range(0, 30).Select(i => $"ClassNumber{i:D2}Name"));
        var entities = EntityExtractor.Extract(content);
        Assert.True(entities.Count <= 20);
    }

    [Fact]
    public void Extract_SortsByLengthDescending()
    {
        var entities = EntityExtractor.Extract("TerminalHost has a MainViewModel and a RavenMemoryStore implementation");
        if (entities.Count >= 2)
        {
            for (int i = 0; i < entities.Count - 1; i++)
                Assert.True(entities[i].Length >= entities[i + 1].Length);
        }
    }

    [Fact]
    public void Extract_RejectsInvalidEntities()
    {
        // Entities with # (markdown headers) should be rejected
        Assert.False(EntityExtractor.IsValidEntity("# Header"));
        // Entities starting with dash/bullet should be rejected
        Assert.False(EntityExtractor.IsValidEntity("- list item"));
        Assert.False(EntityExtractor.IsValidEntity("* bullet point"));
        // Entities with double spaces (prose) should be rejected
        Assert.False(EntityExtractor.IsValidEntity("some  prose text"));
    }

    [Fact]
    public void GenerateHeuristicOneLiner_ExtractsFirstSentence()
    {
        var oneLiner = EntityExtractor.GenerateHeuristicOneLiner(
            "The API uses JWT auth. It also supports OAuth2 and API keys for backward compat.");
        Assert.Equal("The API uses JWT auth", oneLiner);
    }

    [Fact]
    public void GenerateHeuristicOneLiner_TruncatesLongContent()
    {
        var oneLiner = EntityExtractor.GenerateHeuristicOneLiner(
            "This is a very long sentence that goes on and on and on with many many many words that should be truncated");
        Assert.NotNull(oneLiner);
        Assert.EndsWith("...", oneLiner);
    }

    [Fact]
    public void GenerateHeuristicOneLiner_StripsMarkdownHeadings()
    {
        var oneLiner = EntityExtractor.GenerateHeuristicOneLiner("## Architecture Overview\nThe system uses...");
        Assert.NotNull(oneLiner);
        Assert.DoesNotContain("#", oneLiner);
    }

    [Fact]
    public void GenerateHeuristicOneLiner_ReturnsNullForEmptyContent()
    {
        Assert.Null(EntityExtractor.GenerateHeuristicOneLiner(""));
        Assert.Null(EntityExtractor.GenerateHeuristicOneLiner("   "));
    }
}
