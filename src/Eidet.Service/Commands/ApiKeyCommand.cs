using System.Text.Json;
using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class ApiKeyCreateCommand : AsyncCommand<ApiKeyCreateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<NAME>")]
        public string Name { get; set; } = "";

        [CommandOption("--scopes <SCOPES>")]
        public string? Scopes { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var scopes = settings.Scopes?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // Validate scopes
        if (scopes != null)
        {
            foreach (var scope in scopes)
            {
                if (!ApiKeyService.ValidScopes.Contains(scope))
                {
                    AnsiConsole.MarkupLine($"[red]Invalid scope:[/] {Markup.Escape(scope)}");
                    AnsiConsole.MarkupLine($"[dim]Valid scopes: {string.Join(", ", ApiKeyService.ValidScopes)}[/]");
                    return Task.FromResult(1);
                }
            }
        }

        var (rawKey, entry) = ApiKeyService.CreateKey(settings.Name, scopes);

        var config = ConfigManager.Load();
        config.Auth.Enabled = true;
        config.Auth.ApiKeys.Add(entry);
        ConfigManager.Save(config);

        if (settings.Json)
        {
            var json = JsonSerializer.Serialize(new { id = entry.Id, name = entry.Name, key = rawKey, scopes = entry.Scopes },
                new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]API key created:[/] {Markup.Escape(settings.Name)}");
            AnsiConsole.MarkupLine($"  ID:     {entry.Id}");
            AnsiConsole.MarkupLine($"  Scopes: {string.Join(", ", entry.Scopes)}");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Save this key — it will not be shown again:[/]");
            AnsiConsole.MarkupLine($"  [bold]{rawKey}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Auth has been enabled. Use this key in requests:[/]");
            AnsiConsole.MarkupLine("[dim]  curl -H \"Authorization: Bearer <key>\" http://localhost:19380/api/eidet/search?...[/]");
        }

        return Task.FromResult(0);
    }
}

public sealed class ApiKeyListCommand : AsyncCommand<ApiKeyListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();

        if (!config.Auth.Enabled || config.Auth.ApiKeys.Count == 0)
        {
            if (settings.Json)
                Console.WriteLine("[]");
            else
                AnsiConsole.MarkupLine("[dim]No API keys configured. Auth is disabled.[/]");
            return Task.FromResult(0);
        }

        if (settings.Json)
        {
            var json = JsonSerializer.Serialize(
                config.Auth.ApiKeys.Select(k => new { k.Id, k.Name, k.Scopes, k.CreatedAt }),
                new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            return Task.FromResult(0);
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("ID")
            .AddColumn("Name")
            .AddColumn("Scopes")
            .AddColumn("Created");

        foreach (var key in config.Auth.ApiKeys)
        {
            table.AddRow(
                key.Id,
                Markup.Escape(key.Name),
                string.Join(", ", key.Scopes),
                key.CreatedAt.ToString("yyyy-MM-dd"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]Auth enabled: {config.Auth.Enabled}[/]");
        return Task.FromResult(0);
    }
}

public sealed class ApiKeyRevokeCommand : AsyncCommand<ApiKeyRevokeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        public string Id { get; set; } = "";

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var removed = config.Auth.ApiKeys.RemoveAll(k =>
            string.Equals(k.Id, settings.Id, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
        {
            if (settings.Json)
                Console.WriteLine("{\"error\": \"Key not found\"}");
            else
                AnsiConsole.MarkupLine($"[red]API key not found:[/] {Markup.Escape(settings.Id)}");
            return Task.FromResult(1);
        }

        // Disable auth if no keys remain
        if (config.Auth.ApiKeys.Count == 0)
            config.Auth.Enabled = false;

        ConfigManager.Save(config);

        if (settings.Json)
            Console.WriteLine($"{{\"revoked\": true, \"id\": \"{settings.Id}\"}}");
        else
            AnsiConsole.MarkupLine($"[green]API key revoked:[/] {Markup.Escape(settings.Id)}");

        return Task.FromResult(0);
    }
}
