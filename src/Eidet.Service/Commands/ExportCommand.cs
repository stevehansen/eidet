using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class ExportCommand : AsyncCommand<ExportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-r|--repo <REPO>")]
        public string? Repo { get; set; }

        [CommandOption("-o|--output <PATH>")]
        public string? Output { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.Create(config.Storage.RavenUrl, config.Storage.DatabaseName);
        var eidetStore = new RavenEidetStore(store);
        var exportSvc = new ExportService(eidetStore);

        var repoId = Eidet.Core.Domain.RepoIdNormalizer.Normalize(settings.Repo ?? Directory.GetCurrentDirectory());

        try
        {
            var markdown = await exportSvc.ExportMarkdownAsync(repoId, cancellation);

            if (!string.IsNullOrEmpty(settings.Output))
            {
                await File.WriteAllTextAsync(settings.Output, markdown, cancellation);
                AnsiConsole.MarkupLine($"[green]Exported[/] {markdown.Length} chars to {Markup.Escape(settings.Output)}");
            }
            else
            {
                Console.Write(markdown);
            }
        }
        finally
        {
            store.Dispose();
        }

        return 0;
    }
}
