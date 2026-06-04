using System.Text.Json;
using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class OllamaStatusCommand : AsyncCommand<OllamaStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        using var svc = new OllamaService(config.Enrichment.Url);

        var version = await svc.GetVersionAsync(cancellation);
        var models = await svc.ListModelsAsync(cancellation);
        var configuredModel = config.Enrichment.Model;
        var hasConfigured = models.Any(m =>
            m.Name.Equals(configuredModel, StringComparison.OrdinalIgnoreCase) ||
            m.Name.StartsWith(configuredModel + ":", StringComparison.OrdinalIgnoreCase));

        if (settings.Json)
        {
            var json = JsonSerializer.Serialize(new
            {
                available = version != null,
                version,
                url = config.Enrichment.Url,
                enrichmentEnabled = config.Enrichment.Enabled,
                configuredModel,
                modelInstalled = hasConfigured,
                models = models.Select(m => new { m.Name, size = OllamaService.FormatSize(m.Size) }),
            }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            return 0;
        }

        AnsiConsole.WriteLine();
        if (version != null)
        {
            AnsiConsole.MarkupLine($"[green]Ollama[/] v{version} at {config.Enrichment.Url}");
            AnsiConsole.MarkupLine($"  Enrichment:  {(config.Enrichment.Enabled ? "[green]Enabled[/]" : "[dim]Disabled[/]")}");
            AnsiConsole.MarkupLine($"  Model:       {configuredModel} {(hasConfigured ? "[green](installed)[/]" : "[red](not installed)[/]")}");

            if (models.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Installed models:[/]");
                foreach (var m in models)
                    AnsiConsole.MarkupLine($"  {m.Name}  [dim]{OllamaService.FormatSize(m.Size)}[/]");
            }

            if (!hasConfigured)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[yellow]Configured model \"{configuredModel}\" is not installed.[/]");
                AnsiConsole.MarkupLine($"  Pull it:  [dim]eidet ollama pull {configuredModel}[/]");

                var (suggested, isInstalled) = await svc.SuggestModelAsync(cancellation);
                if (isInstalled && suggested != configuredModel)
                    AnsiConsole.MarkupLine($"  Or use:   [dim]eidet config set enrichment.model {suggested}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Ollama not available[/] at {config.Enrichment.Url}");
            AnsiConsole.MarkupLine("[dim]Install Ollama from https://ollama.ai[/]");
        }

        AnsiConsole.WriteLine();
        return 0;
    }
}

public sealed class OllamaPullCommand : AsyncCommand<OllamaPullCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[MODEL]")]
        public string? Model { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        using var svc = new OllamaService(config.Enrichment.Url);

        if (!await svc.IsAvailableAsync(cancellation))
        {
            AnsiConsole.MarkupLine("[red]Ollama is not available.[/]");
            return 1;
        }

        var modelName = settings.Model ?? config.Enrichment.Model;

        // Check if already installed
        if (await svc.HasModelAsync(modelName, cancellation))
        {
            AnsiConsole.MarkupLine($"[green]Model \"{modelName}\" is already installed.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"Pulling [bold]{modelName}[/]...");

        await AnsiConsole.Progress()
            .AutoRefresh(true)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"Downloading {modelName}", maxValue: 100);
                var lastStatus = "";

                await foreach (var progress in svc.PullModelAsync(modelName, cancellation))
                {
                    if (progress.Total > 0)
                    {
                        task.Value = progress.Percent;
                    }

                    if (progress.Status != lastStatus)
                    {
                        task.Description = progress.Status.Length > 60
                            ? progress.Status[..57] + "..."
                            : progress.Status;
                        lastStatus = progress.Status;
                    }
                }

                task.Value = 100;
                task.Description = "Complete";
            });

        AnsiConsole.MarkupLine($"[green]Model \"{modelName}\" pulled successfully.[/]");

        // Auto-configure if this is the first model
        if (!config.Enrichment.Enabled)
        {
            config.Enrichment.Enabled = true;
            config.Enrichment.Model = modelName;
            ConfigManager.Save(config);
            AnsiConsole.MarkupLine("[green]Ollama enrichment enabled automatically.[/]");
        }
        else if (config.Enrichment.Model != modelName)
        {
            AnsiConsole.MarkupLine($"[dim]To use this model: eidet config set enrichment.model {modelName}[/]");
        }

        return 0;
    }
}

public sealed class OllamaListCommand : AsyncCommand<OllamaListCommand.Settings>
{
    public sealed class Settings : CommandSettings { }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        using var svc = new OllamaService(config.Enrichment.Url);

        var models = await svc.ListModelsAsync(cancellation);

        if (models.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No models installed.[/]");
            AnsiConsole.MarkupLine("[dim]Recommended models for Eidet:[/]");
            foreach (var rec in OllamaService.RecommendedModels)
                AnsiConsole.MarkupLine($"  [dim]eidet ollama pull {rec}[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("Model")
            .AddColumn("Size")
            .AddColumn("Status");

        foreach (var m in models)
        {
            var baseName = m.Name.Split(':')[0];
            var isCurrent = baseName.Equals(config.Enrichment.Model, StringComparison.OrdinalIgnoreCase);
            var status = isCurrent ? "[green]active[/]" : "";
            table.AddRow(m.Name, OllamaService.FormatSize(m.Size), status);
        }

        AnsiConsole.Write(table);
        return 0;
    }
}
