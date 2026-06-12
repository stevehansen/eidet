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
    public void GenerateInstructions_ContainsCoreTools()
    {
        var instructions = InstructionsCommand.GenerateInstructions();
        Assert.Contains("eidet_context", instructions);
        Assert.Contains("eidet_recall", instructions);
        Assert.Contains("eidet_store", instructions);
        Assert.Contains("eidet_feedback", instructions);
        Assert.Contains("eidet_forget", instructions);
        Assert.Contains("eidet_link", instructions);
    }

    [Theory]
    [InlineData("eidet_history")]
    [InlineData("eidet_intake")]
    [InlineData("eidet_consolidate")]
    [InlineData("eidet_maintenance")]
    [InlineData("eidet_edit")]
    [InlineData("eidet_pack_export")]
    [InlineData("eidet_pack_import")]
    public void GenerateInstructions_OmitsAdvancedTools(string toolName)
    {
        var instructions = InstructionsCommand.GenerateInstructions();
        Assert.DoesNotContain(toolName, instructions);
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
