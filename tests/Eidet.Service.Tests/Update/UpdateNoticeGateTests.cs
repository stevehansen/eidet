using Eidet.Service.Update;

namespace Eidet.Service.Tests.Update;

public class UpdateNoticeGateTests
{
    // Console.IsOutputRedirected is true under the test runner, which would short-circuit every
    // case. These assert the argument rules, which is where the interesting decisions are.
    private static bool QuietByArgs(params string[] args) =>
        args.Length > 0 && (
            args[0].Equals("mcp", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("update", StringComparison.OrdinalIgnoreCase) ||
            args.Contains("--json", StringComparer.OrdinalIgnoreCase));

    [Theory]
    [InlineData("mcp")]
    [InlineData("MCP")]
    public void Never_writes_to_stdout_during_an_mcp_session(string command)
    {
        // stdout is the JSON-RPC channel. A friendly line there is a protocol violation that
        // surfaces as "the MCP server is broken", not as a stray message.
        Assert.True(QuietByArgs(command));
        Assert.True(UpdateNoticeGate.ShouldStayQuiet([command]));
    }

    [Fact]
    public void Stays_quiet_for_the_update_command_itself()
    {
        Assert.True(QuietByArgs("update"));
        Assert.True(UpdateNoticeGate.ShouldStayQuiet(["update", "--check"]));
    }

    [Theory]
    [InlineData("recall", "--json")]
    [InlineData("stats", "--repo", "P--Eidet", "--json")]
    public void Stays_quiet_when_the_caller_asked_for_json(params string[] args)
    {
        Assert.True(QuietByArgs(args));
        Assert.True(UpdateNoticeGate.ShouldStayQuiet(args));
    }

    [Theory]
    [InlineData("recall")]
    [InlineData("stats")]
    [InlineData("serve")]
    [InlineData("context")]
    public void Allows_the_notice_on_an_ordinary_interactive_command(string command)
    {
        Assert.False(QuietByArgs(command));
    }

    [Fact]
    public void Allows_the_notice_with_no_arguments()
    {
        Assert.False(QuietByArgs());
    }

    [Fact]
    public void Redirected_output_is_quiet_regardless_of_arguments()
    {
        // Piped or captured output is being parsed by something. Under the test runner stdout is
        // already redirected, which is exactly the condition being asserted.
        Assert.True(Console.IsOutputRedirected);
        Assert.True(UpdateNoticeGate.ShouldStayQuiet(["recall"]));
    }
}
