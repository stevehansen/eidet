using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class StatsCommand : AsyncCommand<StatsCommand.Settings>
{
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

        var repoId = settings.Repo ?? Directory.GetCurrentDirectory();
        var normalizedRepoId = Eidet.Core.Domain.RepoIdNormalizer.Normalize(repoId);

        try
        {
            var counts = await eidetStore.GetCountsByTypeAsync(normalizedRepoId, cancellation);
            var total = counts.Values.Sum();

            if (settings.Json)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new { repo = normalizedRepoId, total, counts });
                Console.WriteLine(json);
            }
            else
            {
                AnsiConsole.MarkupLine($"[bold]Eidet Stats[/] — {Markup.Escape(normalizedRepoId)}");
                AnsiConsole.MarkupLine($"  Total: [bold]{total}[/] memories");
                foreach (var (type, count) in counts.OrderBy(kv => kv.Key))
                {
                    if (count > 0)
                        AnsiConsole.MarkupLine($"  {type}: {count}");
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
