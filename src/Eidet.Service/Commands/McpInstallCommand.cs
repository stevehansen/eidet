using Eidet.Service.Mcp;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

/// <summary>
/// Registers eidet as an MCP server in a single AI client's configuration.
/// Prefers the client's own CLI (e.g. <c>claude mcp add</c>, <c>codex mcp add</c>),
/// falls back to writing the config file directly. With no argument, prompts
/// interactively from the list of detected clients.
/// </summary>
public sealed class McpInstallCommand : AsyncCommand<McpInstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[client]")]
        public string? Client { get; set; }

        [CommandOption("--all")]
        public bool All { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var targets = await ResolveTargetsAsync(settings, ct);
        if (targets.Count == 0) return 1;

        var results = new List<(string Name, bool Ok, string Detail)>();
        foreach (var client in targets)
        {
            var status = await client.CheckAsync(ct);
            if (status == McpInstallStatus.Configured)
            {
                // Idempotent: already-configured isn't a failure.
                results.Add((client.Name, true, "already configured"));
                continue;
            }
            var (ok, detail) = await client.InstallAsync(ct);
            results.Add((client.Name, ok, detail));
        }

        if (settings.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                results = results.Select(r => new { r.Name, success = r.Ok, detail = r.Detail }),
            }));
        }
        else
        {
            foreach (var r in results)
            {
                var color = r.Ok ? "green" : "yellow";
                AnsiConsole.MarkupLine($"  [{color}]{r.Name}[/]: {Markup.Escape(r.Detail)}");
            }
        }

        return results.Any(r => r.Ok) ? 0 : 1;
    }

    private static async Task<List<McpClient>> ResolveTargetsAsync(Settings settings, CancellationToken ct)
    {
        if (settings.All)
        {
            var available = new List<McpClient>();
            foreach (var client in McpClientRegistry.All)
                if (await client.CheckAsync(ct) != McpInstallStatus.NotAvailable)
                    available.Add(client);
            return available;
        }

        if (!string.IsNullOrEmpty(settings.Client))
        {
            var picked = McpClientRegistry.FindByName(settings.Client);
            if (picked == null)
            {
                AnsiConsole.MarkupLine($"[red]Unknown client '{settings.Client}'.[/]");
                AnsiConsole.MarkupLine("Known: " + string.Join(", ", McpClientRegistry.All.Select(c => c.Name)));
                return [];
            }
            return [picked];
        }

        // No client specified: prompt interactively from detected ones.
        var detected = new List<McpClient>();
        foreach (var client in McpClientRegistry.All)
        {
            if (await client.CheckAsync(ct) != McpInstallStatus.NotAvailable)
                detected.Add(client);
        }

        if (detected.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No MCP-capable clients detected on this machine.[/]");
            AnsiConsole.MarkupLine("Known: " + string.Join(", ", McpClientRegistry.All.Select(c => c.Name)));
            return [];
        }

        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[red]No client specified and terminal is non-interactive.[/]");
            AnsiConsole.MarkupLine("Pass a client name (e.g. `eidet mcp install claude-code`) or `--all`.");
            return [];
        }

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Install eidet MCP into which client?")
                .AddChoices(detected.Select(c => c.Name).Concat(new[] { "(all detected)", "(cancel)" })));

        if (choice == "(cancel)") return [];
        if (choice == "(all detected)") return detected;
        return [detected.First(c => c.Name == choice)];
    }
}
