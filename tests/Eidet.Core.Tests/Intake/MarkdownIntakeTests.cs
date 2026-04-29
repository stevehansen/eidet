using Eidet.Core.Intake;

namespace Eidet.Core.Tests.Intake;

public class MarkdownIntakeTests
{
    [Fact]
    public void SplitByHeadings_SingleSection_ReturnsSingle()
    {
        var result = MarkdownIntake.SplitByHeadings("## Title\nSome content here that is long enough to pass the minimum length check.");
        Assert.Single(result);
        Assert.Contains("Title", result[0].Content);
    }

    [Fact]
    public void SplitByHeadings_MultipleSections_SplitsCorrectly()
    {
        var md = """
                 ## Section One
                 Content for section one is here and it's long enough.

                 ## Section Two
                 Content for section two is here and it's long enough.
                 """;

        var result = MarkdownIntake.SplitByHeadings(md);
        Assert.Equal(2, result.Count);
        Assert.Contains("section", result[0].Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("one", result[0].Tags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SplitByHeadings_NoHeadings_ReturnsWholeContent()
    {
        var result = MarkdownIntake.SplitByHeadings("Just a paragraph of text without any markdown headings.");
        Assert.Single(result);
        Assert.Empty(result[0].Tags);
    }

    [Fact]
    public void SplitByHeadings_ShortSections_Filtered()
    {
        var md = """
                 ## A
                 Short.

                 ## Real Section
                 This section has enough content to pass the minimum length threshold.
                 """;

        var result = MarkdownIntake.SplitByHeadings(md);
        Assert.Single(result);
        Assert.Contains("Real", result[0].Content);
    }

    [Fact]
    public void SplitByHeadings_H1AndH3_AllRecognized()
    {
        var md = """
                 # Top Level Heading
                 Top level content that is long enough for processing.

                 ### Sub Heading Here
                 Sub heading content that is also long enough to be kept.
                 """;

        var result = MarkdownIntake.SplitByHeadings(md);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SplitByHeadings_Empty_ReturnsEmpty()
    {
        var result = MarkdownIntake.SplitByHeadings("");
        Assert.Empty(result);
    }

    [Fact]
    public void SplitByHeadings_WhitespaceOnly_ReturnsEmpty()
    {
        var result = MarkdownIntake.SplitByHeadings("   \n\n  ");
        Assert.Empty(result);
    }

    [Fact]
    public void SplitByHeadings_TagsExtractedFromHeading()
    {
        var md = "## RavenDB Configuration\nDetailed configuration steps for the RavenDB database connection.";
        var result = MarkdownIntake.SplitByHeadings(md);

        Assert.Single(result);
        Assert.Contains("ravendb", result[0].Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("configuration", result[0].Tags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TagsFromFileName_StripsExtension_LowerCased()
    {
        var tags = MarkdownIntake.TagsFromFileName("ravendb_config-notes.md");

        Assert.Contains("ravendb", tags);
        Assert.Contains("config", tags);
        Assert.Contains("notes", tags);
        Assert.DoesNotContain("md", tags);
    }
}
