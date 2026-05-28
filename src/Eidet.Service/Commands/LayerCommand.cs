using System.Text.Json;
using System.Text.Json.Serialization;
using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class LayerSyncCommand : AsyncCommand<LayerSyncCommand.Settings>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<PATH>")]
        public string Path { get; set; } = "";

        [CommandOption("--layer-id <ID>")]
        public string? LayerId { get; set; }

        [CommandOption("--preview")]
        public bool Preview { get; set; }

        [CommandOption("--keep-stale")]
        public bool KeepStale { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.CreateFromConfig(config);
        var eidetStore = new RavenEidetStore(store);
        var layerSvc = new LayerService(eidetStore);
        var syncSvc = new LayerSyncService(eidetStore, layerSvc, new MemoryService(eidetStore));

        try
        {
            if (settings.Preview)
            {
                var preview = await syncSvc.PreviewAsync(settings.Path, settings.LayerId, cancellation);

                if (settings.Json)
                {
                    Console.Write(JsonSerializer.Serialize(preview, JsonOptions));
                    return 0;
                }

                AnsiConsole.MarkupLine($"[bold]Sync Preview[/]: {Markup.Escape(preview.PackName)} v{Markup.Escape(preview.PackVersion)}");
                AnsiConsole.MarkupLine($"Layer: [cyan]{Markup.Escape(preview.LayerId)}[/]");
                if (preview.CurrentVersion != null)
                    AnsiConsole.MarkupLine($"Current version: [dim]{Markup.Escape(preview.CurrentVersion)}[/]");
                else
                    AnsiConsole.MarkupLine("Current version: [dim](new layer)[/]");

                AnsiConsole.WriteLine();

                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("Action");
                table.AddColumn("Count");
                table.AddRow("[green]Add[/]", preview.Added.ToString());
                table.AddRow("[yellow]Update[/]", preview.Updated.ToString());
                table.AddRow("[red]Remove[/]", preview.Removed.ToString());
                table.AddRow("[dim]Unchanged[/]", preview.Unchanged.ToString());
                AnsiConsole.Write(table);

                if (preview.Entries.Count > 0 && preview.Entries.Any(e => e.Action != SyncAction.Unchanged))
                {
                    AnsiConsole.WriteLine();
                    var detailTable = new Table().Border(TableBorder.Simple);
                    detailTable.AddColumn("Action");
                    detailTable.AddColumn("Type");
                    detailTable.AddColumn("Description");

                    foreach (var entry in preview.Entries.Where(e => e.Action != SyncAction.Unchanged).OrderBy(e => e.Action))
                    {
                        var actionMarkup = entry.Action switch
                        {
                            SyncAction.Add => "[green]+ Add[/]",
                            SyncAction.Update => "[yellow]~ Update[/]",
                            SyncAction.Remove => "[red]- Remove[/]",
                            _ => "[dim]Unchanged[/]",
                        };
                        detailTable.AddRow(actionMarkup, entry.Type.ToString(), Markup.Escape(entry.OneLiner ?? entry.Id));
                    }

                    AnsiConsole.Write(detailTable);
                }
            }
            else
            {
                var result = await syncSvc.SyncAsync(settings.Path, settings.LayerId, !settings.KeepStale, cancellation);

                if (settings.Json)
                {
                    Console.Write(JsonSerializer.Serialize(result, JsonOptions));
                    return 0;
                }

                AnsiConsole.MarkupLine($"[green]Synced[/] {Markup.Escape(result.PackName)} v{Markup.Escape(result.PackVersion)} → [cyan]{Markup.Escape(result.LayerId)}[/]");
                AnsiConsole.MarkupLine($"  [green]+{result.Added}[/] added, [yellow]~{result.Updated}[/] updated, [red]-{result.Removed}[/] removed, [dim]{result.Unchanged} unchanged[/]");
                if (result.StaleKept > 0)
                    AnsiConsole.MarkupLine($"  [dim]{result.StaleKept} stale entries kept (--keep-stale)[/]");
            }
        }
        finally
        {
            store.Dispose();
        }

        return 0;
    }
}

public sealed class LayerListCommand : AsyncCommand<LayerListCommand.Settings>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public sealed class Settings : CommandSettings
    {
        [CommandOption("-r|--repo <REPO>")]
        public string? Repo { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.CreateFromConfig(config);
        var eidetStore = new RavenEidetStore(store);
        var layerSvc = new LayerService(eidetStore);

        var repoId = Eidet.Core.Domain.RepoIdNormalizer.Normalize(settings.Repo ?? Directory.GetCurrentDirectory());

        try
        {
            var layers = await layerSvc.GetApplicableLayersAsync(repoId, ct: cancellation);

            if (settings.Json)
            {
                Console.Write(JsonSerializer.Serialize(new { repo = repoId, layers }, JsonOptions));
                return 0;
            }

            if (layers.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No layers mounted for this repo.[/]");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("ID");
            table.AddColumn("Name");
            table.AddColumn("Type");
            table.AddColumn("Version");
            table.AddColumn("Source");
            table.AddColumn("Synced");

            foreach (var layer in layers)
            {
                table.AddRow(
                    Markup.Escape(layer.Id),
                    Markup.Escape(layer.Name),
                    layer.Type.ToString(),
                    Markup.Escape(layer.Version ?? "-"),
                    Markup.Escape(layer.SourcePath ?? "-"),
                    layer.LastSyncedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-");
            }

            AnsiConsole.Write(table);
        }
        finally
        {
            store.Dispose();
        }

        return 0;
    }
}
