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
        var rows = new List<(string Name, McpInstallStatus Status, string? ConfigPath, string? Unsupported)>();
        foreach (var client in McpClientRegistry.All)
            rows.Add((client.Name, await client.CheckAsync(ct), client.ConfigPath, client.Unsupported));

        if (settings.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                clients = rows.Select(r => new { name = r.Name, status = r.Status.ToString(), configPath = r.ConfigPath, unsupported = r.Unsupported }),
            }));
            return 0;
        }

        var table = new Table().AddColumns("Client", "Status", "Config");
        foreach (var (name, status, path, unsupported) in rows)
        {
            var statusCell = status switch
            {
                McpInstallStatus.Configured when unsupported != null => "[red]Configured — remove it[/]",
                McpInstallStatus.NotConfigured when unsupported != null => "[dim]Unsupported[/]",
                McpInstallStatus.Configured => "[green]Configured[/]",
                McpInstallStatus.NotConfigured => "[yellow]Not configured[/]",
                _ => "[dim]Not installed[/]",
            };
            table.AddRow(name, statusCell, Markup.Escape(path ?? "—"));
        }

        AnsiConsole.Write(table);

        foreach (var r in rows.Where(r => r.Unsupported != null && r.Status == McpInstallStatus.Configured))
            AnsiConsole.MarkupLine($"\n[red]Remove eidet from {Markup.Escape(r.ConfigPath ?? r.Name)}[/]: {Markup.Escape(r.Unsupported!)}.");

        if (rows.Any(r => r.Status == McpInstallStatus.NotConfigured && r.Unsupported == null))
            AnsiConsole.MarkupLine("\n[dim]Run `eidet mcp install <client>` to register eidet.[/]");

        return 0;
    }
}
