using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Eidet.Service.Commands;

/// <summary>
/// Every repo holding live memories, with the count and — where the recorded path still resolves
/// somewhere else — the repo it would be written to today.
///
/// This is what makes <c>eidet repo rehome</c> usable: a stranded namespace is only visible if
/// something enumerates repos honestly, and its whole problem is that nobody goes looking for a repo
/// they did not know existed.
/// </summary>
public sealed class RepoListCommand : AsyncCommand<RepoListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--stranded")]
        [Description("Only repos whose recorded path resolves to a different repo today")]
        public bool StrandedOnly { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.CreateFromConfig(config);
        var eidetStore = new RavenEidetStore(store, config);
        var usage = new UsageTracker(store);

        try
        {
            var counts = await eidetStore.GetLiveCountsByRepoAsync(cancellation);
            var paths = await usage.GetAllRepoPathsAsync();

            var rows = counts
                .Select(kv =>
                {
                    var path = paths.GetValueOrDefault(kv.Key);
                    // Only a path that still exists can be resolved; a worktree whose directory is gone
                    // is reported without a target rather than guessed at.
                    var resolved = path is not null ? RepoIdNormalizer.Normalize(RepoPathResolver.Resolve(path)) : null;
                    var stranded = resolved is not null && !string.Equals(resolved, kv.Key, StringComparison.OrdinalIgnoreCase);
                    return (Repo: kv.Key, Count: kv.Value, Path: path, ResolvesTo: stranded ? resolved : null);
                })
                .Where(r => !settings.StrandedOnly || r.ResolvesTo is not null)
                .OrderByDescending(r => r.Count)
                .ToList();

            if (settings.Json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    repos = rows.Select(r => new { repo = r.Repo, live = r.Count, path = r.Path, resolvesTo = r.ResolvesTo }),
                }));
                return 0;
            }

            if (rows.Count == 0)
            {
                AnsiConsole.MarkupLine(settings.StrandedOnly
                    ? "[green]No stranded repos.[/]"
                    : "[yellow]No repos with live memories.[/]");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Repo");
            table.AddColumn(new TableColumn("Live").RightAligned());
            table.AddColumn("Resolves to");

            foreach (var row in rows)
            {
                table.AddRow(
                    Markup.Escape(row.Repo),
                    row.Count.ToString(),
                    row.ResolvesTo is null ? "[dim]—[/]" : $"[yellow]{Markup.Escape(row.ResolvesTo)}[/]");
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[dim]{rows.Count} repos, {rows.Sum(r => r.Count)} live memories[/]");

            var stranded = rows.Count(r => r.ResolvesTo is not null);
            if (stranded > 0)
                AnsiConsole.MarkupLine(
                    $"[yellow]{stranded}[/] repo(s) resolve elsewhere — move with [bold]eidet repo rehome --from <repo> --to <repo>[/] (try --dry-run first).");

            return 0;
        }
        finally
        {
            store.Dispose();
        }
    }
}
