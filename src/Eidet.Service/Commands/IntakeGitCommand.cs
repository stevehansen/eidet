using Eidet.Core.Configuration;
using Eidet.Core.Intake.Git;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;
using Eidet.Core.Domain;

namespace Eidet.Service.Commands;

public sealed class IntakeGitCommand : AsyncCommand<IntakeGitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[PATH]")]
        public string? Path { get; set; }

        [CommandOption("--dry-run")]
        public bool DryRun { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }

        [CommandOption("--since <SHA>")]
        public string? Since { get; set; }

        [CommandOption("--max-commits <COUNT>")]
        public int MaxCommits { get; set; } = 500;

        [CommandOption("--all-commits")]
        public bool AllCommits { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.CreateFromConfig(config);
        var eidetStore = new RavenEidetStore(store);
        var intakeSvc = new IntakeService(eidetStore, new MemoryService(eidetStore));

        var projectPath = settings.Path ?? Directory.GetCurrentDirectory();
        var repoId = RepoPathResolver.Resolve(projectPath);
        var options = new GitIntakeOptions(settings.Since, settings.MaxCommits, settings.AllCommits);

        try
        {
            var result = await intakeSvc.IngestGitAsync(repoId, projectPath, options, settings.DryRun, cancellation);

            if (settings.Json)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    newCount = result.NewCount,
                    skippedCount = result.SkippedCount,
                });
                Console.WriteLine(json);
            }
            else
            {
                var mode = settings.DryRun ? "Would mine" : "Mined";
                AnsiConsole.MarkupLine($"[bold]{mode}:[/] {result.NewCount} new, {result.SkippedCount} skipped");

                foreach (var item in result.Items.Take(20))
                {
                    var status = item.WasSkipped ? $"[yellow]SKIP ({Markup.Escape(item.SkipReason ?? "")})[/]" : "[green]NEW[/]";
                    AnsiConsole.MarkupLine($"  {status} {Markup.Escape(item.Source)} → {item.Type}: {Markup.Escape(Core.StringUtils.Truncate(item.Content, 60))}");
                }
                if (result.Items.Count > 20)
                    AnsiConsole.MarkupLine($"  ... and {result.Items.Count - 20} more");
            }
        }
        finally
        {
            store.Dispose();
        }

        return 0;
    }
}
