using System.Text.Json;
using Eidet.Core.MemoryTool;

namespace Eidet.Core.Tests.MemoryTool;

public class MemoryCommandParseTests
{
    private static MemoryCommand Parse(string json) =>
        MemoryCommand.Parse(JsonSerializer.Deserialize<JsonElement>(json));

    // ─── Each wire shape maps to its typed command ────────────────────────

    [Fact]
    public void Parse_View()
    {
        var cmd = Assert.IsType<MemoryCommand.View>(Parse("""{"command":"view","path":"/memories/notes.md"}"""));
        Assert.Equal("/memories/notes.md", cmd.Path.Value);
        Assert.Null(cmd.Range);
    }

    [Fact]
    public void Parse_View_WithRange()
    {
        var cmd = Assert.IsType<MemoryCommand.View>(
            Parse("""{"command":"view","path":"/memories/notes.md","view_range":[2,5]}"""));
        Assert.Equal((2, 5), cmd.Range);
    }

    [Fact]
    public void Parse_Create()
    {
        var cmd = Assert.IsType<MemoryCommand.Create>(
            Parse("""{"command":"create","path":"/memories/notes.md","file_text":"hello\n"}"""));
        Assert.Equal("hello\n", cmd.FileText);
    }

    [Fact]
    public void Parse_StrReplace()
    {
        var cmd = Assert.IsType<MemoryCommand.StrReplace>(
            Parse("""{"command":"str_replace","path":"/memories/notes.md","old_str":"a","new_str":"b"}"""));
        Assert.Equal("a", cmd.OldStr);
        Assert.Equal("b", cmd.NewStr);
    }

    [Fact]
    public void Parse_StrReplace_NewStrOptional()
    {
        var cmd = Assert.IsType<MemoryCommand.StrReplace>(
            Parse("""{"command":"str_replace","path":"/memories/notes.md","old_str":"a"}"""));
        Assert.Null(cmd.NewStr);
    }

    [Fact]
    public void Parse_Insert()
    {
        var cmd = Assert.IsType<MemoryCommand.Insert>(
            Parse("""{"command":"insert","path":"/memories/notes.md","insert_line":0,"insert_text":"top"}"""));
        Assert.Equal(0, cmd.InsertLine);
        Assert.Equal("top", cmd.InsertText);
    }

    [Fact]
    public void Parse_Delete()
    {
        var cmd = Assert.IsType<MemoryCommand.Delete>(Parse("""{"command":"delete","path":"/memories/old.md"}"""));
        Assert.Equal("/memories/old.md", cmd.Path.Value);
    }

    [Fact]
    public void Parse_Rename()
    {
        var cmd = Assert.IsType<MemoryCommand.Rename>(
            Parse("""{"command":"rename","old_path":"/memories/a.md","new_path":"/memories/b.md"}"""));
        Assert.Equal("/memories/a.md", cmd.OldPath.Value);
        Assert.Equal("/memories/b.md", cmd.NewPath.Value);
    }

    // ─── Malformed input never throws — it becomes Invalid ───────────────

    [Theory]
    [InlineData("""{"command":"launch_missiles","path":"/memories/x"}""")]
    [InlineData("""{"path":"/memories/x"}""")]
    [InlineData("""{"command":"view"}""")]
    [InlineData("""{"command":"create","path":"/memories/x"}""")]
    [InlineData("""{"command":"str_replace","path":"/memories/x"}""")]
    [InlineData("""{"command":"insert","path":"/memories/x","insert_text":"t"}""")]
    [InlineData("""{"command":"insert","path":"/memories/x","insert_line":1}""")]
    [InlineData("""{"command":"rename","old_path":"/memories/x"}""")]
    [InlineData("""{"command":"view","path":"/memories/x","view_range":[1]}""")]
    [InlineData("""{"command":"view","path":"/memories/x","view_range":[1.5,2]}""")]
    [InlineData("""{"command":"view","path":"/memories/x","view_range":["1","2"]}""")]
    [InlineData("""{"command":"insert","path":"/memories/x","insert_line":1.5,"insert_text":"t"}""")]
    [InlineData("""{"command":"insert","path":"/memories/x","insert_line":"1","insert_text":"t"}""")]
    [InlineData(""" "just a string" """)]
    [InlineData("42")]
    public void Parse_MalformedInput_YieldsInvalid(string json)
    {
        var cmd = Parse(json);
        var invalid = Assert.IsType<MemoryCommand.Invalid>(cmd);
        Assert.NotEmpty(invalid.Message);
    }

    [Fact]
    public void Parse_TraversalPath_YieldsInvalid_NotException()
    {
        var cmd = Parse("""{"command":"view","path":"/memories/../../etc/passwd"}""");
        var invalid = Assert.IsType<MemoryCommand.Invalid>(cmd);
        Assert.Contains("traversal", invalid.Message, StringComparison.OrdinalIgnoreCase);
    }
}
