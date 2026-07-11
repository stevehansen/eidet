using System.ComponentModel;
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

        [CommandOption("-f|--format <FORMAT>")]
        [Description("Export format: dump (memory dump), pack (shareable pack), or agents (AGENTS.md interop file)")]
        public string? Format { get; set; }

        [CommandOption("--pack-id <ID>")]
        [Description("Pack ID for pack export")]
        public string? PackId { get; set; }

        [CommandOption("--name <NAME>")]
        [Description("Pack name for pack export")]
        public string? Name { get; set; }

        [CommandOption("--version <VERSION>")]
        [Description("Pack version for pack export")]
        public string? Version { get; set; }

        [CommandOption("--author <AUTHOR>")]
        [Description("Pack author for pack export")]
        public string? Author { get; set; }

        [CommandOption("--description <DESC>")]
        [Description("Pack description for pack export")]
        public string? PackDescription { get; set; }

        [CommandOption("--packages <PACKAGES>")]
        [Description("Comma-separated applicable packages for pack export")]
        public string? Packages { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.CreateFromConfig(config);
        var eidetStore = new RavenEidetStore(store);
        var exportSvc = new ExportService(eidetStore, new MemoryService(eidetStore));

        var repoId = Eidet.Core.Domain.RepoIdNormalizer.Normalize(settings.Repo ?? Directory.GetCurrentDirectory());

        try
        {
            if (string.Equals(settings.Format, "pack", StringComparison.OrdinalIgnoreCase))
            {
                return await ExportPack(exportSvc, repoId, settings, cancellation);
            }

            // Default: memory dump; "agents" renders the AGENTS.md interop shape.
            var markdown = string.Equals(settings.Format, "agents", StringComparison.OrdinalIgnoreCase)
                ? await exportSvc.ExportAgentsMdAsync(repoId, cancellation)
                : await exportSvc.ExportMarkdownAsync(repoId, cancellation);

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

    private static async Task<int> ExportPack(ExportService exportSvc, string repoId, Settings settings, CancellationToken ct)
    {
        var packId = settings.PackId;
        if (string.IsNullOrEmpty(packId))
        {
            AnsiConsole.MarkupLine("[red]--pack-id is required for pack export[/]");
            return 1;
        }

        var name = settings.Name ?? packId;
        var version = settings.Version ?? "1.0.0";
        var author = settings.Author ?? Environment.UserName;
        var packages = string.IsNullOrEmpty(settings.Packages)
            ? null
            : settings.Packages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        var pack = await exportSvc.ExportPackAsync(repoId, packId, name, version, author,
            applicablePackages: packages, ct: ct);
        pack.Description = settings.PackDescription;

        // Default output path based on extension
        var output = settings.Output ?? $"{packId}.md";

        await exportSvc.ExportPackToFileAsync(pack, output, ct);
        AnsiConsole.MarkupLine($"[green]Exported[/] {pack.Entries.Count} memories to {Markup.Escape(output)}");

        return 0;
    }
}
