using Eidet.Service.Mcp;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

/// <summary>
/// Show MCP client registration status: which AI clients are present on this
/// machine and which already have eidet configured.
/// </summary>
public sealed class McpListCommand : AsyncCommand<McpListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var rows = new List<(string Name, McpInstallStatus Status, string? ConfigPath)>();
        foreach (var client in McpClientRegistry.All)
            rows.Add((client.Name, await client.CheckAsync(ct), client.ConfigPath));

        if (settings.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                clients = rows.Select(r => new { name = r.Name, status = r.Status.ToString(), configPath = r.ConfigPath }),
            }));
            return 0;
        }

        var table = new Table().AddColumns("Client", "Status", "Config");
        foreach (var (name, status, path) in rows)
        {
            var statusCell = status switch
            {
                McpInstallStatus.Configured => "[green]Configured[/]",
                McpInstallStatus.NotConfigured => "[yellow]Not configured[/]",
                _ => "[dim]Not installed[/]",
            };
            table.AddRow(name, statusCell, Markup.Escape(path ?? "—"));
        }

        AnsiConsole.Write(table);

        if (rows.Any(r => r.Status == McpInstallStatus.NotConfigured))
            AnsiConsole.MarkupLine("\n[dim]Run `eidet mcp install <client>` to register eidet.[/]");

        return 0;
    }
}
