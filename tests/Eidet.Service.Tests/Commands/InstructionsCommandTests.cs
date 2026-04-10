using Eidet.Service.Commands;

namespace Eidet.Service.Tests.Commands;

public class InstructionsCommandTests
{
    [Fact]
    public void GenerateInstructions_ContainsVersion()
    {
        var instructions = InstructionsCommand.GenerateInstructions();
        Assert.Contains(Eidet.Core.EidetVersion.Current, instructions);
    }

    [Fact]
    public void GenerateInstructions_ContainsAllTools()
    {
        var instructions = InstructionsCommand.GenerateInstructions();
        Assert.Contains("eidet_store", instructions);
        Assert.Contains("eidet_recall", instructions);
        Assert.Contains("eidet_context", instructions);
        Assert.Contains("eidet_forget", instructions);
        Assert.Contains("eidet_feedback", instructions);
        Assert.Contains("eidet_history", instructions);
        Assert.Contains("eidet_intake", instructions);
        Assert.Contains("eidet_link", instructions);
        Assert.Contains("eidet_consolidate", instructions);
        Assert.Contains("eidet_maintenance", instructions);
        Assert.Contains("eidet_export", instructions);
        Assert.Contains("eidet_pack_export", instructions);
        Assert.Contains("eidet_pack_import", instructions);
    }

    [Fact]
    public void GenerateInstructions_ContainsMemoryTypes()
    {
        var instructions = InstructionsCommand.GenerateInstructions();
        Assert.Contains("observation", instructions);
        Assert.Contains("insight", instructions);
        Assert.Contains("procedure", instructions);
        Assert.Contains("heuristic", instructions);
    }

    [Fact]
    public void GenerateInstructions_ContainsFeedbackGuidance()
    {
        var instructions = InstructionsCommand.GenerateInstructions();
        Assert.Contains("echo", instructions);
        Assert.Contains("fizzle", instructions);
    }

    [Fact]
    public void GetGlobalClaudeMdPath_ReturnsValidPath()
    {
        var path = InstructionsCommand.GetGlobalClaudeMdPath();
        Assert.NotNull(path);
        Assert.Contains(".claude", path);
        Assert.True(path.EndsWith("CLAUDE.md"), $"Path should end with CLAUDE.md, was: {path}");
    }
}
