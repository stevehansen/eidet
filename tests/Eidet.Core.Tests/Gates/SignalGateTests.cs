using Eidet.Core.Domain;
using Eidet.Core.Gates;

namespace Eidet.Core.Tests.Gates;

public class SignalGateTests
{
    [Fact]
    public void Check_PassesGoodContent()
    {
        var result = SignalGate.Check("The deployment pipeline uses GitHub Actions with a matrix build strategy");
        Assert.True(result.Passed);
    }

    [Fact]
    public void Check_BlocksEmptyContent()
    {
        Assert.False(SignalGate.Check("").Passed);
        Assert.False(SignalGate.Check("   ").Passed);
    }

    [Fact]
    public void Check_BlocksShortContent()
    {
        var result = SignalGate.Check("too short");
        Assert.False(result.Passed);
        Assert.Contains("too short", result.Reason);
    }

    [Theory]
    [InlineData("tests passed")]
    [InlineData("it works")]
    [InlineData("done")]
    [InlineData("no changes")]
    [InlineData("build succeeded")]
    [InlineData("modified")]
    public void Check_BlocksLowSignalPhrases(string content)
    {
        // These are all < 20 chars, so they'll be caught by the length gate first
        // That's the correct TerminalHost behavior — length check before phrase check
        var result = SignalGate.Check(content);
        Assert.False(result.Passed);
    }

    [Theory]
    [InlineData("tests passed.")]
    [InlineData("no changes.")]
    public void Check_BlocksLowSignalPhrasesWithTrailingPeriod(string content)
    {
        var result = SignalGate.Check(content);
        Assert.False(result.Passed);
    }

    [Theory]
    [InlineData("I will check the database connection next")]
    [InlineData("Let me look at the configuration file")]
    [InlineData("I'm going to run the tests now")]
    public void Check_BlocksAgentSelfTalkForObservations(string content)
    {
        var result = SignalGate.Check(content, MemoryType.Observation);
        Assert.False(result.Passed);
        Assert.Contains("self-talk", result.Reason);
    }

    [Fact]
    public void Check_AllowsSelfTalkPhraseForNonObservations()
    {
        // Self-talk check only applies to Observations
        var result = SignalGate.Check("I will always run migrations before tests in this repo", MemoryType.Heuristic);
        Assert.True(result.Passed);
    }
}
