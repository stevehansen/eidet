using Eidet.Core.Configuration;
using Eidet.Core.Domain;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class StoreCommand : AsyncCommand<StoreCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<CONTENT>")]
        public string Content { get; set; } = "";

        [CommandOption("-t|--type <TYPE>")]
        public string Type { get; set; } = "observation";

        [CommandOption("-r|--repo <REPO>")]
        public string? Repo { get; set; }

        [CommandOption("--tags <TAGS>")]
        public string? Tags { get; set; }

        [CommandOption("-i|--importance <IMPORTANCE>")]
        public float? Importance { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!Enum.TryParse<MemoryType>(settings.Type, true, out var type))
        {
            AnsiConsole.MarkupLine($"[red]Invalid type:[/] {Markup.Escape(settings.Type)}. Use: observation, insight, procedure, heuristic.");
            return 1;
        }

        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.CreateFromConfig(config);
        var eidetStore = new RavenEidetStore(store);
        IHookRunner hookRunner = config.Hooks.PreStore.Count > 0 || config.Hooks.PostStore.Count > 0
            ? new HookRunner(config.Hooks) : NullHookRunner.Instance;
        var svc = new MemoryService(eidetStore, hooks: hookRunner);

        var repoId = settings.Repo ?? Directory.GetCurrentDirectory();
        var tags = settings.Tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        try
        {
            var result = await svc.StoreAsync(new StoreOptions(repoId, settings.Content, type)
            {
                Tags = tags,
                Importance = settings.Importance ?? 0.5f,
                Source = "user",
            }, cancellation);

            if (settings.Json)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new { result.Success, result.Id, result.Reason, result.DuplicateId });
                Console.WriteLine(json);
            }
            else if (result.Success)
                AnsiConsole.MarkupLine($"[green]Stored:[/] {Markup.Escape(result.Id!)}");
            else if (result.DuplicateId != null)
                AnsiConsole.MarkupLine($"[yellow]Duplicate:[/] {Markup.Escape(result.DuplicateId)}");
            else
                AnsiConsole.MarkupLine($"[red]Rejected:[/] {Markup.Escape(result.Reason ?? "unknown")}");
        }
        finally
        {
            store.Dispose();
        }

        return 0;
    }
}
