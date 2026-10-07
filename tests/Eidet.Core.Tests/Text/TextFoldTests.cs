using Eidet.Core.Text;

namespace Eidet.Core.Tests.Text;

public class TextFoldTests
{
    [Theory]
    [InlineData("scratch-pad 0.5.0 adds option B. It also fixes the cache.", "scratch-pad 0.5.0 adds option B.")]
    [InlineData("Plugins read state through $.store.get first. Then they render.", "Plugins read state through $.store.get first.")]
    [InlineData("Edit WriteValidator.cs to add the gate. Then rebuild.", "Edit WriteValidator.cs to add the gate.")]
    public void FirstSentence_DotWithoutFollowingSpace_DoesNotEndTheSentence(string text, string expected) =>
        Assert.Equal(expected, TextFold.FirstSentence(text));

    [Theory]
    [InlineData("Fold the reply, e.g. to its first sentence. Then stop.", "Fold the reply, e.g. to its first sentence.")]
    [InlineData("Lists, tables, etc. are kept. Prose is folded.", "Lists, tables, etc. are kept.")]
    [InlineData("Lexical vs. vector recall differ. Fusion blends them.", "Lexical vs. vector recall differ.")]
    [InlineData("See Fig. 3 for the ladder. It has three rungs.", "See Fig. 3 for the ladder.")]
    public void FirstSentence_Abbreviation_DoesNotEndTheSentence(string text, string expected) =>
        Assert.Equal(expected, TextFold.FirstSentence(text));

    [Fact]
    public void FirstSentence_StopInsideInlineCode_DoesNotEndTheSentence() =>
        Assert.Equal(
            "Run `git log -1. --stat` before pushing.",
            TextFold.FirstSentence("Run `git log -1. --stat` before pushing. Then tag."));

    [Theory]
    [InlineData("记忆是本地的。服务不联网。", "记忆是本地的。")]
    [InlineData("最初の文です！次の文です。", "最初の文です！")]
    [InlineData("これは何？答えです。", "これは何？")]
    public void FirstSentence_CjkStop_EndsTheSentenceWithoutSpace(string text, string expected) =>
        Assert.Equal(expected, TextFold.FirstSentence(text));

    [Fact]
    public void FirstSentence_LineBreak_EndsTheSentence() =>
        Assert.Equal("## Tech Stack", TextFold.FirstSentence("## Tech Stack\n- .NET 10 (latest SDK)\n- RavenDB 7.x"));

    [Fact]
    public void FirstSentence_ClosingQuoteOrBold_StaysWithItsStop() =>
        Assert.Equal("He said \"done.\"", TextFold.FirstSentence("He said \"done.\" Then he left."));

    [Fact]
    public void FirstSentence_CutInsideBold_ClosesIt() =>
        Assert.Equal("**Use TextFold.**", TextFold.FirstSentence("**Use TextFold. Not Truncate** for recall."));

    [Fact]
    public void FirstSentence_NoStop_ReturnsWholeTrimmedText() =>
        Assert.Equal("no stop here", TextFold.FirstSentence("  no stop here  "));

    [Fact]
    public void Fit_TextWithinBudget_IsReturnedTrimmed() =>
        Assert.Equal("Short. Text.", TextFold.Fit("  Short. Text.  ", 20));

    [Fact]
    public void Fit_KeepsWholeSentencesThatFit_AndMarksTheRest()
    {
        // The first two sentences are 27 chars; the third takes it to 57.
        var folded = TextFold.Fit("Bump 0.5.0 first. Then tag. Then push the release branch.", 30);

        Assert.Equal("Bump 0.5.0 first. Then tag. …", folded);
    }

    [Fact]
    public void Fit_FirstSentenceOverBudget_FallsBackToCharCut() =>
        Assert.Equal("A very long…", TextFold.Fit("A very long first sentence that exceeds it. Short.", 11));

    [Fact]
    public void Fit_LineBreaksAreBoundaries_AndKeptAsWritten() =>
        Assert.Equal(
            "## Tech Stack\n- .NET 10 (latest SDK) …",
            TextFold.Fit("## Tech Stack\n- .NET 10 (latest SDK)\n- RavenDB 7.x for hybrid search and embeddings", 40));

    [Fact]
    public void Fit_NeverCutsInsideACodeFence()
    {
        var text = "Run this:\n```\nsafe dotnet build. Then\nsafe dotnet test\n```\nAfterwards check the log.";

        Assert.Equal("Run this: …", TextFold.Fit(text, 40));
    }
}
