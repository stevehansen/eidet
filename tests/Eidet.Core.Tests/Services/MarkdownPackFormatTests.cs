using Eidet.Core.Domain;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Services;

public class MarkdownPackFormatTests
{
    // ─── Serialize ──────────────────────────────────────────────────────

    [Fact]
    public void Serialize_IncludesFrontmatter()
    {
        var pack = CreateTestPack();
        var md = MarkdownPackFormat.Serialize(pack);

        Assert.StartsWith("---\n", md);
        Assert.Contains("title: React Best Practices", md);
        Assert.Contains("  id: react-best-practices", md);
        Assert.Contains("  version: 1.0.0", md);
        Assert.Contains("  author: steve", md);
        Assert.Contains("  applicablePackages: [react, react-dom]", md);
    }

    [Fact]
    public void Serialize_IncludesDescription()
    {
        var pack = CreateTestPack();
        pack.Description = "Curated memory pack for React";
        var md = MarkdownPackFormat.Serialize(pack);

        Assert.Contains("description: Curated memory pack for React", md);
        // Also appears in body after H1
        Assert.Contains("Curated memory pack for React\n", md);
    }

    [Fact]
    public void Serialize_GroupsByType()
    {
        var pack = CreateTestPack();
        var md = MarkdownPackFormat.Serialize(pack);

        Assert.Contains("## Insights", md);
        Assert.Contains("## Heuristics", md);
        // Insights should appear before Heuristics
        Assert.True(md.IndexOf("## Insights") < md.IndexOf("## Heuristics"));
    }

    [Fact]
    public void Serialize_MemoryHeadingsAreH3()
    {
        var pack = CreateTestPack();
        var md = MarkdownPackFormat.Serialize(pack);

        Assert.Contains("### Call hooks at top level", md);
        Assert.Contains("### Always memoize context values", md);
    }

    [Fact]
    public void Serialize_IncludesMetadataComments()
    {
        var pack = CreateTestPack();
        var md = MarkdownPackFormat.Serialize(pack);

        Assert.Contains("<!-- eidet: importance=0.85 confidence=0.75 tags=react,hooks -->", md);
    }

    [Fact]
    public void Serialize_IncludesEntityComments()
    {
        var pack = CreateTestPack();
        var md = MarkdownPackFormat.Serialize(pack);

        Assert.Contains("<!-- eidet-entities: useState, useEffect -->", md);
    }

    [Fact]
    public void Serialize_IncludesContent()
    {
        var pack = CreateTestPack();
        var md = MarkdownPackFormat.Serialize(pack);

        Assert.Contains("React hooks should be called at the top level.", md);
    }

    [Fact]
    public void Serialize_OmitsEmptyTypeGroups()
    {
        var pack = CreateTestPack(); // Has Insight + Heuristic only
        var md = MarkdownPackFormat.Serialize(pack);

        Assert.DoesNotContain("## Observations", md);
        Assert.DoesNotContain("## Procedures", md);
    }

    [Fact]
    public void Serialize_OrdersByImportanceWithinGroup()
    {
        var pack = new EidetPack
        {
            Id = "test", Name = "Test", Version = "1.0.0", Author = "test",
            Entries =
            [
                CreateEntry(MemoryType.Insight, "Low priority", importance: 0.3f, oneLiner: "Low"),
                CreateEntry(MemoryType.Insight, "High priority", importance: 0.9f, oneLiner: "High"),
            ]
        };
        var md = MarkdownPackFormat.Serialize(pack);

        Assert.True(md.IndexOf("### High") < md.IndexOf("### Low"));
    }

    [Fact]
    public void Serialize_IncludesEnrichmentComments()
    {
        var entry = CreateEntry(MemoryType.Insight, "Test content", oneLiner: "Test");
        entry.Summary = "A brief summary";
        entry.ForesightHint = "May be useful for performance tuning";

        var pack = new EidetPack
        {
            Id = "test", Name = "Test", Version = "1.0.0", Author = "test",
            Entries = [entry]
        };
        var md = MarkdownPackFormat.Serialize(pack);

        Assert.Contains("<!-- eidet-summary: A brief summary -->", md);
        Assert.Contains("<!-- eidet-foresight: May be useful for performance tuning -->", md);
    }

    [Fact]
    public void Serialize_IncludesProvenanceWhenNotDefault()
    {
        var entry = CreateEntry(MemoryType.Insight, "Test content", oneLiner: "Test");
        entry.Provenance = MemoryProvenance.UserStated;

        var pack = new EidetPack
        {
            Id = "test", Name = "Test", Version = "1.0.0", Author = "test",
            Entries = [entry]
        };
        var md = MarkdownPackFormat.Serialize(pack);

        Assert.Contains("provenance=userstated", md);
    }

    [Fact]
    public void Serialize_OmitsDefaultProvenance()
    {
        var entry = CreateEntry(MemoryType.Insight, "Test content", oneLiner: "Test");
        entry.Provenance = MemoryProvenance.AgentInferred;

        var pack = new EidetPack
        {
            Id = "test", Name = "Test", Version = "1.0.0", Author = "test",
            Entries = [entry]
        };
        var md = MarkdownPackFormat.Serialize(pack);

        Assert.DoesNotContain("provenance=", md);
    }

    [Fact]
    public void Serialize_CollectsAllTagsInFrontmatter()
    {
        var pack = new EidetPack
        {
            Id = "test", Name = "Test", Version = "1.0.0", Author = "test",
            Entries =
            [
                CreateEntry(MemoryType.Insight, "A", tags: ["react", "hooks"]),
                CreateEntry(MemoryType.Insight, "B", tags: ["react", "performance"]),
            ]
        };
        var md = MarkdownPackFormat.Serialize(pack);

        // Frontmatter should contain union of all tags
        Assert.Contains("tags: [hooks, performance, react]", md);
    }

    // ─── Deserialize ────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_ParsesFrontmatter()
    {
        var md = """
            ---
            title: React Best Practices
            eidet:
              id: react-best-practices
              version: 1.0.0
              author: steve
              applicablePackages: [react, react-dom]
            ---

            # React Best Practices

            ## Insights

            ### Test insight
            <!-- eidet: importance=0.85 confidence=0.75 tags=react -->

            Some content here.
            """;

        var pack = MarkdownPackFormat.Deserialize(md);

        Assert.Equal("react-best-practices", pack.Id);
        Assert.Equal("React Best Practices", pack.Name);
        Assert.Equal("1.0.0", pack.Version);
        Assert.Equal("steve", pack.Author);
        Assert.Equal(["react", "react-dom"], pack.ApplicablePackages);
    }

    [Fact]
    public void Deserialize_ParsesEntries()
    {
        var md = """
            ---
            title: Test
            eidet:
              id: test-pack
              version: 1.0.0
              author: test
            ---

            # Test

            ## Insights

            ### Call hooks at top level
            <!-- eidet: importance=0.85 confidence=0.75 tags=react,hooks -->
            <!-- eidet-entities: useState, useEffect -->

            React hooks should be called at the top level.

            ## Heuristics

            ### Always memoize context values
            <!-- eidet: importance=0.70 confidence=0.80 tags=react,performance -->

            Wrap context provider values in useMemo.
            """;

        var pack = MarkdownPackFormat.Deserialize(md);

        Assert.Equal(2, pack.Entries.Count);

        var insight = pack.Entries[0];
        Assert.Equal(MemoryType.Insight, insight.Type);
        Assert.Equal("Call hooks at top level", insight.OneLiner);
        Assert.Equal(0.85f, insight.Importance);
        Assert.Equal(0.75f, insight.Confidence);
        Assert.Equal(["react", "hooks"], insight.Tags);
        Assert.Equal(["useState", "useEffect"], insight.Entities);
        Assert.Equal("React hooks should be called at the top level.", insight.Content);

        var heuristic = pack.Entries[1];
        Assert.Equal(MemoryType.Heuristic, heuristic.Type);
        Assert.Equal("Always memoize context values", heuristic.OneLiner);
        Assert.Equal(0.70f, heuristic.Importance);
        Assert.Equal(0.80f, heuristic.Confidence);
    }

    [Fact]
    public void Deserialize_SetsLayerIdFromBundleId()
    {
        var md = """
            ---
            title: Test
            eidet:
              id: my-bundle
              version: 1.0.0
              author: test
            ---

            # Test

            ## Insights

            ### A memory
            <!-- eidet: importance=0.5 confidence=0.7 -->

            Content here.
            """;

        var pack = MarkdownPackFormat.Deserialize(md);
        Assert.Equal("bundle:my-bundle", pack.Entries[0].LayerId);
    }

    [Fact]
    public void Deserialize_SetsProvenanceToBundle()
    {
        var md = """
            ---
            title: Test
            eidet:
              id: test
              version: 1.0.0
              author: test
            ---

            # Test

            ## Insights

            ### A memory
            <!-- eidet: importance=0.5 confidence=0.7 -->

            Content here.
            """;

        var pack = MarkdownPackFormat.Deserialize(md);
        Assert.Equal(MemoryProvenance.Bundle, pack.Entries[0].Provenance);
        Assert.Equal("markdown-pack", pack.Entries[0].Source);
    }

    [Fact]
    public void Deserialize_ParsesEnrichmentComments()
    {
        var md = """
            ---
            title: Test
            eidet:
              id: test
              version: 1.0.0
              author: test
            ---

            # Test

            ## Insights

            ### Test entry
            <!-- eidet: importance=0.5 confidence=0.7 -->
            <!-- eidet-summary: A short summary of the content -->
            <!-- eidet-foresight: May help with debugging -->

            The actual content.
            """;

        var pack = MarkdownPackFormat.Deserialize(md);
        Assert.Equal("A short summary of the content", pack.Entries[0].Summary);
        Assert.Equal("May help with debugging", pack.Entries[0].ForesightHint);
    }

    [Fact]
    public void Deserialize_HandlesMultiParagraphContent()
    {
        var md = """
            ---
            title: Test
            eidet:
              id: test
              version: 1.0.0
              author: test
            ---

            # Test

            ## Procedures

            ### How to debug SSR
            <!-- eidet: importance=0.6 confidence=0.9 tags=ssr,debugging -->

            First paragraph about the issue.

            Second paragraph with more detail.

            1. Step one
            2. Step two
            3. Step three
            """;

        var pack = MarkdownPackFormat.Deserialize(md);
        var content = pack.Entries[0].Content;

        Assert.Contains("First paragraph", content);
        Assert.Contains("Second paragraph", content);
        Assert.Contains("1. Step one", content);
        Assert.Equal(MemoryType.Procedure, pack.Entries[0].Type);
    }

    [Fact]
    public void Deserialize_HandlesCustomProvenance()
    {
        var md = """
            ---
            title: Test
            eidet:
              id: test
              version: 1.0.0
              author: test
            ---

            # Test

            ## Insights

            ### User-stated insight
            <!-- eidet: importance=0.8 confidence=0.9 provenance=userstated source=manual-curation -->

            This was manually curated.
            """;

        var pack = MarkdownPackFormat.Deserialize(md);
        Assert.Equal(MemoryProvenance.UserStated, pack.Entries[0].Provenance);
        Assert.Equal("manual-curation", pack.Entries[0].Source);
    }

    [Fact]
    public void Deserialize_NoFrontmatter_ReturnsEmptyPack()
    {
        var md = """
            # Just a heading

            Some content without frontmatter.
            """;

        var pack = MarkdownPackFormat.Deserialize(md);
        Assert.Equal("", pack.Id);
        Assert.Empty(pack.Entries);
    }

    [Fact]
    public void Deserialize_ParsesCreatedAt()
    {
        var md = """
            ---
            title: Test
            eidet:
              id: test
              version: 1.0.0
              author: test
              createdAt: 2026-04-15T10:30:00.0000000Z
            ---

            # Test
            """;

        var pack = MarkdownPackFormat.Deserialize(md);
        Assert.Equal(new DateTime(2026, 4, 15, 10, 30, 0, DateTimeKind.Utc), pack.CreatedAt);
    }

    // ─── Round-Trip ─────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_PreservesPackMetadata()
    {
        var original = CreateTestPack();
        original.Description = "A description";

        var md = MarkdownPackFormat.Serialize(original);
        var restored = MarkdownPackFormat.Deserialize(md);

        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Version, restored.Version);
        Assert.Equal(original.Author, restored.Author);
        Assert.Equal(original.Description, restored.Description);
        Assert.Equal(original.ApplicablePackages, restored.ApplicablePackages);
    }

    [Fact]
    public void RoundTrip_PreservesEntryContent()
    {
        var original = CreateTestPack();
        var md = MarkdownPackFormat.Serialize(original);
        var restored = MarkdownPackFormat.Deserialize(md);

        Assert.Equal(original.Entries.Count, restored.Entries.Count);

        for (int i = 0; i < original.Entries.Count; i++)
        {
            var orig = original.Entries[i];
            // Find by type and oneLiner since ordering within groups is by importance
            var rest = restored.Entries.First(e => e.OneLiner == orig.OneLiner);
            Assert.Equal(orig.Type, rest.Type);
            Assert.Equal(orig.Content, rest.Content);
            Assert.Equal(orig.Importance, rest.Importance, precision: 2);
            Assert.Equal(orig.Confidence, rest.Confidence, precision: 2);
            Assert.Equal(orig.Tags, rest.Tags);
            Assert.Equal(orig.Entities, rest.Entities);
        }
    }

    [Fact]
    public void RoundTrip_PreservesEnrichment()
    {
        var entry = CreateEntry(MemoryType.Insight, "Test content", oneLiner: "Test");
        entry.Summary = "Brief summary";
        entry.ForesightHint = "Future relevance note";

        var pack = new EidetPack
        {
            Id = "test", Name = "Test", Version = "1.0.0", Author = "test",
            Entries = [entry]
        };

        var md = MarkdownPackFormat.Serialize(pack);
        var restored = MarkdownPackFormat.Deserialize(md);

        Assert.Equal("Brief summary", restored.Entries[0].Summary);
        Assert.Equal("Future relevance note", restored.Entries[0].ForesightHint);
    }

    // ─── Content With Markdown ─────────────────────────────────────────

    [Fact]
    public void Deserialize_ContentWithH3Headings_PreservesAsContent()
    {
        var md = """
            ---
            title: Test
            eidet:
              id: test
              version: 1.0.0
              author: test
            ---

            # Test

            ## Procedures

            ### How to set up the project
            <!-- eidet: importance=0.8 confidence=0.9 tags=setup -->

            Follow these steps:

            ### Step 1: Clone the repo

            Run `git clone` to get the code.

            ### Step 2: Install dependencies

            Run `npm install` in the project root.

            ### Final verification
            <!-- eidet: importance=0.5 confidence=0.7 tags=setup -->

            Check that everything works by running `npm test`.
            """;

        var pack = MarkdownPackFormat.Deserialize(md);

        // Should have 2 memories (the two with eidet comments), not 4
        Assert.Equal(2, pack.Entries.Count);

        // First memory should contain the H3 headings as content
        var first = pack.Entries[0];
        Assert.Equal("How to set up the project", first.OneLiner);
        Assert.Contains("### Step 1: Clone the repo", first.Content);
        Assert.Contains("### Step 2: Install dependencies", first.Content);
        Assert.Contains("Run `npm install`", first.Content);

        // Second memory starts at the next eidet-tagged heading
        var second = pack.Entries[1];
        Assert.Equal("Final verification", second.OneLiner);
    }

    [Fact]
    public void Deserialize_ContentWithH2Headings_NonTypeH2PreservedAsContent()
    {
        var md = """
            ---
            title: Test
            eidet:
              id: test
              version: 1.0.0
              author: test
            ---

            # Test

            ## Insights

            ### Architecture overview
            <!-- eidet: importance=0.9 confidence=0.8 tags=architecture -->

            The system has several layers:

            ## Data Layer

            Handles persistence and caching.

            ## Service Layer

            Contains business logic.

            ## Heuristics

            ### Always validate inputs
            <!-- eidet: importance=0.7 confidence=0.85 tags=validation -->

            Never trust external input.
            """;

        var pack = MarkdownPackFormat.Deserialize(md);

        // "Data Layer" and "Service Layer" are NOT known types, so they're content
        Assert.Equal(2, pack.Entries.Count);

        var insight = pack.Entries[0];
        Assert.Equal(MemoryType.Insight, insight.Type);
        Assert.Contains("## Data Layer", insight.Content);
        Assert.Contains("## Service Layer", insight.Content);

        var heuristic = pack.Entries[1];
        Assert.Equal(MemoryType.Heuristic, heuristic.Type);
        Assert.Equal("Always validate inputs", heuristic.OneLiner);
    }

    [Fact]
    public void RoundTrip_ContentWithMarkdownHeadings_Preserved()
    {
        var contentWithHeadings = """
            The project structure:

            ## Frontend

            React application with TypeScript.

            ### Components

            All components live in src/components.

            ## Backend

            .NET API with RavenDB.
            """;

        var pack = new EidetPack
        {
            Id = "test", Name = "Test", Version = "1.0.0", Author = "test",
            Entries =
            [
                new MemoryEntry
                {
                    Type = MemoryType.Insight,
                    Content = contentWithHeadings.Trim(),
                    OneLiner = "Project structure overview",
                    Importance = 0.8f,
                    Confidence = 0.7f,
                    Tags = ["architecture"],
                    Provenance = MemoryProvenance.AgentInferred,
                    CreatedAt = DateTime.UtcNow,
                }
            ]
        };

        var md = MarkdownPackFormat.Serialize(pack);
        var restored = MarkdownPackFormat.Deserialize(md);

        Assert.Single(restored.Entries);
        Assert.Contains("## Frontend", restored.Entries[0].Content);
        Assert.Contains("### Components", restored.Entries[0].Content);
        Assert.Contains("## Backend", restored.Entries[0].Content);
    }

    [Fact]
    public void IsMemoryBoundary_WithEidetComment_ReturnsTrue()
    {
        var lines = new[]
        {
            "### Some heading",
            "<!-- eidet: importance=0.5 confidence=0.7 -->",
            "",
            "Content here."
        };
        Assert.True(MarkdownPackFormat.IsMemoryBoundary(lines, 0));
    }

    [Fact]
    public void IsMemoryBoundary_WithoutEidetComment_ReturnsFalse()
    {
        var lines = new[]
        {
            "### Some heading",
            "",
            "This is just content under a heading.",
            "More content."
        };
        Assert.False(MarkdownPackFormat.IsMemoryBoundary(lines, 0));
    }

    [Fact]
    public void IsMemoryBoundary_BlankLineThenEidetComment_ReturnsTrue()
    {
        var lines = new[]
        {
            "### Some heading",
            "",
            "<!-- eidet: importance=0.5 confidence=0.7 -->",
            "Content here."
        };
        Assert.True(MarkdownPackFormat.IsMemoryBoundary(lines, 0));
    }

    // ─── Internal Helpers ───────────────────────────────────────────────

    [Theory]
    [InlineData("Observations", MemoryType.Observation)]
    [InlineData("Insights", MemoryType.Insight)]
    [InlineData("Procedures", MemoryType.Procedure)]
    [InlineData("Heuristics", MemoryType.Heuristic)]
    public void PluralToType_Maps(string plural, MemoryType expected)
    {
        Assert.Equal(expected, MarkdownPackFormat.PluralToType(plural));
    }

    [Theory]
    [InlineData(MemoryType.Observation, "Observations")]
    [InlineData(MemoryType.Insight, "Insights")]
    [InlineData(MemoryType.Procedure, "Procedures")]
    [InlineData(MemoryType.Heuristic, "Heuristics")]
    public void TypeToPlural_Maps(MemoryType type, string expected)
    {
        Assert.Equal(expected, MarkdownPackFormat.TypeToPlural(type));
    }

    [Fact]
    public void ParseFrontmatter_HandlesNestedKeys()
    {
        var fm = MarkdownPackFormat.ParseFrontmatter("""
            title: Test
            eidet:
              id: my-id
              version: 2.0.0
            tags: [a, b]
            """);

        Assert.Equal("Test", fm["title"]);
        Assert.Equal("my-id", fm["eidet.id"]);
        Assert.Equal("2.0.0", fm["eidet.version"]);
        Assert.Equal("[a, b]", fm["tags"]);
    }

    [Fact]
    public void ParseInlineList_BracketedFormat()
    {
        var result = MarkdownPackFormat.ParseInlineList("[react, react-dom, next]");
        Assert.Equal(["react", "react-dom", "next"], result);
    }

    [Fact]
    public void ParseInlineList_EmptyReturnsEmptyList()
    {
        Assert.Empty(MarkdownPackFormat.ParseInlineList(null));
        Assert.Empty(MarkdownPackFormat.ParseInlineList(""));
        Assert.Empty(MarkdownPackFormat.ParseInlineList("  "));
    }

    [Fact]
    public void YamlEscape_QuotesSpecialChars()
    {
        Assert.Equal("simple", MarkdownPackFormat.YamlEscape("simple"));
        Assert.Equal("\"has: colon\"", MarkdownPackFormat.YamlEscape("has: colon"));
        Assert.Equal("\"has # hash\"", MarkdownPackFormat.YamlEscape("has # hash"));
    }

    [Fact]
    public void HtmlCommentEscape_RoundTrips()
    {
        var original = "some -- dashes -- here";
        var escaped = MarkdownPackFormat.EscapeHtmlComment(original);
        var unescaped = MarkdownPackFormat.UnescapeHtmlComment(escaped);
        Assert.Equal(original, unescaped);
        Assert.DoesNotContain("--", escaped);
    }

    [Fact]
    public void SplitFrontmatter_NoFrontmatter()
    {
        var (fm, body) = MarkdownPackFormat.SplitFrontmatter("# Just a heading\nContent");
        Assert.Equal("", fm);
        Assert.Equal("# Just a heading\nContent", body);
    }

    [Fact]
    public void SplitFrontmatter_WithFrontmatter()
    {
        var (fm, body) = MarkdownPackFormat.SplitFrontmatter("---\ntitle: Test\n---\n# Heading");
        Assert.Equal("title: Test", fm);
        Assert.Equal("# Heading", body);
    }

    [Fact]
    public void ParseMetaPairs_ExtractsKeyValues()
    {
        var pairs = MarkdownPackFormat.ParseMetaPairs("importance=0.85 confidence=0.75 tags=react,hooks");
        Assert.Equal("0.85", pairs["importance"]);
        Assert.Equal("0.75", pairs["confidence"]);
        Assert.Equal("react,hooks", pairs["tags"]);
    }

    [Fact]
    public void Deserialize_DerivedFromRoundTrips()
    {
        var entry = CreateEntry(MemoryType.Insight, "Derived content", oneLiner: "Derived");
        entry.DerivedFrom = ["memories/test/observation/abc123", "memories/test/observation/def456"];

        var pack = new EidetPack
        {
            Id = "test", Name = "Test", Version = "1.0.0", Author = "test",
            Entries = [entry]
        };

        var md = MarkdownPackFormat.Serialize(pack);
        Assert.Contains("derivedFrom=memories/test/observation/abc123,memories/test/observation/def456", md);

        var restored = MarkdownPackFormat.Deserialize(md);
        Assert.Equal(entry.DerivedFrom, restored.Entries[0].DerivedFrom);
    }

    // ─── Test Helpers ───────────────────────────────────────────────────

    private static EidetPack CreateTestPack()
    {
        return new EidetPack
        {
            Id = "react-best-practices",
            Name = "React Best Practices",
            Version = "1.0.0",
            Author = "steve",
            CreatedAt = new DateTime(2026, 4, 15, 10, 0, 0, DateTimeKind.Utc),
            ApplicablePackages = ["react", "react-dom"],
            Entries =
            [
                new MemoryEntry
                {
                    Type = MemoryType.Insight,
                    Content = "React hooks should be called at the top level.",
                    OneLiner = "Call hooks at top level",
                    Importance = 0.85f,
                    Confidence = 0.75f,
                    Tags = ["react", "hooks"],
                    Entities = ["useState", "useEffect"],
                    Provenance = MemoryProvenance.AgentInferred,
                    CreatedAt = DateTime.UtcNow,
                },
                new MemoryEntry
                {
                    Type = MemoryType.Heuristic,
                    Content = "Wrap context provider values in useMemo.",
                    OneLiner = "Always memoize context values",
                    Importance = 0.70f,
                    Confidence = 0.80f,
                    Tags = ["react", "performance"],
                    Provenance = MemoryProvenance.AgentInferred,
                    CreatedAt = DateTime.UtcNow,
                },
            ],
        };
    }

    private static MemoryEntry CreateEntry(
        MemoryType type, string content,
        float importance = 0.5f, string? oneLiner = null,
        List<string>? tags = null)
    {
        return new MemoryEntry
        {
            Type = type,
            Content = content,
            OneLiner = oneLiner,
            Importance = importance,
            Confidence = 0.7f,
            Tags = tags ?? [],
            Provenance = MemoryProvenance.AgentInferred,
            CreatedAt = DateTime.UtcNow,
        };
    }
}
