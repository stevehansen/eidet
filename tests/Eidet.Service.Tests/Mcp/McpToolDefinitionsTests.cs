using Eidet.Service.Mcp;

namespace Eidet.Service.Tests.Mcp;

public class McpToolDefinitionsTests
{
    [Fact]
    public void GetAll_Returns13Tools()
    {
        var tools = McpToolDefinitions.GetAll();
        Assert.Equal(13, tools.Count);
    }

    [Fact]
    public void GetAll_AllNamesUnique()
    {
        var tools = McpToolDefinitions.GetAll();
        var names = tools.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void GetAll_AllNamesStartWithEidet()
    {
        var tools = McpToolDefinitions.GetAll();
        Assert.All(tools, t => Assert.StartsWith("eidet_", t.Name));
    }

    [Fact]
    public void GetAll_AllHaveDescriptions()
    {
        var tools = McpToolDefinitions.GetAll();
        Assert.All(tools, t => Assert.False(string.IsNullOrEmpty(t.Description)));
    }

    [Fact]
    public void GetAll_AllHaveInputSchema()
    {
        var tools = McpToolDefinitions.GetAll();
        Assert.All(tools, t =>
        {
            Assert.NotNull(t.InputSchema);
            Assert.Equal("object", t.InputSchema["type"]?.ToString());
        });
    }

    [Theory]
    [InlineData("eidet_store")]
    [InlineData("eidet_recall")]
    [InlineData("eidet_context")]
    [InlineData("eidet_forget")]
    [InlineData("eidet_feedback")]
    [InlineData("eidet_history")]
    [InlineData("eidet_intake")]
    [InlineData("eidet_link")]
    [InlineData("eidet_consolidate")]
    [InlineData("eidet_maintenance")]
    [InlineData("eidet_edit")]
    [InlineData("eidet_pack_export")]
    [InlineData("eidet_pack_import")]
    public void GetAll_ContainsTool(string toolName)
    {
        var tools = McpToolDefinitions.GetAll();
        Assert.Contains(tools, t => t.Name == toolName);
    }

    [Fact]
    public void Store_RequiresContentAndType()
    {
        var tools = McpToolDefinitions.GetAll();
        var store = tools.First(t => t.Name == "eidet_store");
        var required = store.InputSchema["required"]!.AsArray();
        Assert.Contains(required, r => r!.ToString() == "content");
        Assert.Contains(required, r => r!.ToString() == "type");
    }

    [Fact]
    public void Recall_RequiresQuery()
    {
        var tools = McpToolDefinitions.GetAll();
        var recall = tools.First(t => t.Name == "eidet_recall");
        var required = recall.InputSchema["required"]!.AsArray();
        Assert.Contains(required, r => r!.ToString() == "query");
    }

    [Fact]
    public void Forget_RequiresId()
    {
        var tools = McpToolDefinitions.GetAll();
        var forget = tools.First(t => t.Name == "eidet_forget");
        var required = forget.InputSchema["required"]!.AsArray();
        Assert.Contains(required, r => r!.ToString() == "id");
    }

    [Fact]
    public void Edit_RequiresId()
    {
        var tools = McpToolDefinitions.GetAll();
        var edit = tools.First(t => t.Name == "eidet_edit");
        var required = edit.InputSchema["required"]!.AsArray();
        Assert.Contains(required, r => r!.ToString() == "id");
    }

    [Fact]
    public void Edit_HasOptionalFields()
    {
        var tools = McpToolDefinitions.GetAll();
        var edit = tools.First(t => t.Name == "eidet_edit");
        var props = edit.InputSchema["properties"]!.AsObject();
        Assert.True(props.ContainsKey("content"));
        Assert.True(props.ContainsKey("tags"));
        Assert.True(props.ContainsKey("importance"));
        Assert.True(props.ContainsKey("confidence"));
        Assert.True(props.ContainsKey("type"));
    }

    [Fact]
    public void PackExport_RequiresPackId()
    {
        var tools = McpToolDefinitions.GetAll();
        var tool = tools.First(t => t.Name == "eidet_pack_export");
        var required = tool.InputSchema["required"]!.AsArray();
        Assert.Contains(required, r => r!.ToString() == "pack_id");
    }

    [Fact]
    public void PackImport_RequiresPath()
    {
        var tools = McpToolDefinitions.GetAll();
        var tool = tools.First(t => t.Name == "eidet_pack_import");
        var required = tool.InputSchema["required"]!.AsArray();
        Assert.Contains(required, r => r!.ToString() == "path");
    }
}
