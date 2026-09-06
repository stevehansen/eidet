using Eidet.Core.Configuration;
using Eidet.Core.LooseEnds;
using Eidet.Core.LooseEnds.Promotion;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;
using Eidet.Core.Domain;

namespace Eidet.Service.Commands;

public sealed class ContextCommand : AsyncCommand<ContextCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-r|--repo <REPO>")]
        public string? Repo { get; set; }

        [CommandOption("-t|--tokens <TOKENS>")]
        public int? MaxTokens { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.CreateFromConfig(config);
        var eidetStore = new RavenEidetStore(store, config);
        var layerSvc = new LayerService(eidetStore);
        var memorySvc = new MemoryService(eidetStore, layerSvc);

        // Wire Loose Ends so `eidet context` shows the same wake-up slice + open-count addendum the
        // agent receives via MCP/REST — otherwise this debug view silently diverges from real context.
        var looseEndSvc = new LooseEndService(
            new RavenLooseEndStore(store), new MemoryServicePromotionAdapter(memorySvc), TimeProvider.System);
        memorySvc.LooseEnds = looseEndSvc;

        var repoId = RepoPathResolver.Resolve(settings.Repo ?? Directory.GetCurrentDirectory());
        var maxTokens = settings.MaxTokens ?? 600;

        try
        {
            var normalizedRepoId = Eidet.Core.Domain.RepoIdNormalizer.Normalize(repoId);
            var contextText = await memorySvc.GetContextAsync(normalizedRepoId, maxTokens, cancellation);
            var resolved = await layerSvc.ResolveScopeAsync(normalizedRepoId, crossRepo: true, ct: cancellation);
            var layers = resolved.MountedLayers;
            var scope = resolved.RepoIds;

            if (settings.Json)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    repo = normalizedRepoId,
                    maxTokens,
                    context = contextText.Trim(),
                    estimatedTokens = (int)Math.Ceiling(contextText.Length / 4.0),
                    layers = layers.Select(l => new { l.Id, l.Name, type = l.Type.ToString() }),
                    crossRepoScope = scope,
                }, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
                });
                Console.WriteLine(json);
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold]Eidet Context[/] — {Markup.Escape(normalizedRepoId)}");
                AnsiConsole.MarkupLine($"  [dim]Max tokens: {maxTokens} | Estimated: ~{(int)Math.Ceiling(contextText.Length / 4.0)} tokens[/]");
                AnsiConsole.WriteLine();

                // Show context block
                var panel = new Panel(Markup.Escape(contextText.Trim()))
                {
                    Border = BoxBorder.Rounded,
                    Header = new PanelHeader("L0 + L1 Context"),
                    Padding = new Padding(1, 0),
                };
                AnsiConsole.Write(panel);

                // Show layers
                if (layers.Count > 0)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[bold]Mounted Layers[/] ({layers.Count}):");
                    foreach (var layer in layers)
                        AnsiConsole.MarkupLine($"  [blue]{Markup.Escape(layer.Name)}[/] ({layer.Type}) — {Markup.Escape(layer.Id)}");
                }

                // Show cross-repo scope
                if (scope.Count > 1)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[bold]Cross-Repo Scope[/] ({scope.Count} repos):");
                    foreach (var r in scope)
                    {
                        var marker = string.Equals(r, normalizedRepoId, StringComparison.OrdinalIgnoreCase) ? " [dim](primary)[/]" : "";
                        AnsiConsole.MarkupLine($"  {Markup.Escape(r)}{marker}");
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine($"\n[dim]No cross-repo links detected.[/]");
                }
            }
        }
        finally
        {
            store.Dispose();
        }

        return 0;
    }
}
