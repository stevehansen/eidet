using System.ComponentModel;
using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

/// <summary>
/// Moves a repo namespace's memories into another repo — the repair for memories banked from a git
/// worktree before repo identity resolved one. Hand-aimed on purpose: the source path is usually gone
/// by the time anyone notices, so only the operator can say where its memories belong.
/// </summary>
public sealed class RepoRehomeCommand : AsyncCommand<RepoRehomeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--from <REPO>")]
        [Description("Repo id or path to move memories out of")]
        public string From { get; set; } = "";

        [CommandOption("--to <REPO>")]
        [Description("Repo id or path to move memories into")]
        public string To { get; set; } = "";

        [CommandOption("--dry-run")]
        [Description("Report what would move without writing anything")]
        public bool DryRun { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(settings.From) || string.IsNullOrWhiteSpace(settings.To))
        {
            AnsiConsole.MarkupLine("[red]Both --from and --to are required.[/]");
            return 1;
        }

        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.CreateFromConfig(config);
        var eidetStore = new RavenEidetStore(store, config);
        var memory = new MemoryService(eidetStore);
        var rehome = new RepoRehomeService(eidetStore, memory);

        try
        {
            var result = await rehome.RehomeAsync(settings.From, settings.To, settings.DryRun, cancellation);

            if (settings.Json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    from = result.From,
                    to = result.To,
                    moved = result.Moved,
                    folded = result.Folded,
                    dryRun = settings.DryRun,
                }));
                return 0;
            }

            var verb = settings.DryRun ? "Would move" : "Moved";
            AnsiConsole.MarkupLine(
                $"[bold]{verb} {result.Moved}[/] memories — {Markup.Escape(result.From)} → {Markup.Escape(result.To)}");
            if (result.Folded > 0)
                AnsiConsole.MarkupLine($"  [dim]{result.Folded} folded — target already held that content, retired here[/]");
            if (settings.DryRun)
                AnsiConsole.MarkupLine("  [dim]Dry run — nothing was written.[/]");
            else if (result.Moved > 0)
                AnsiConsole.MarkupLine($"  [dim]Originals retired with a reason naming {Markup.Escape(result.To)}.[/]");

            return 0;
        }
        finally
        {
            store.Dispose();
        }
    }
}
