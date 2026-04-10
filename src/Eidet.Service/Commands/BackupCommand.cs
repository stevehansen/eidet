using System.Text.Json;
using Eidet.Core.Configuration;
using Eidet.Core.Services;
using Eidet.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class BackupCreateCommand : AsyncCommand<BackupCreateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-o|--output <PATH>")]
        public string? OutputDir { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.CreateFromConfig(config);

        try
        {
            var svc = new BackupService(store);
            var backupDir = settings.OutputDir ?? BackupService.GetBackupDir(config.Backup);
            var (path, manifest) = await svc.CreateBackupAsync(backupDir, cancellation);

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { path, manifest.DocumentCount, manifest.CreatedAt },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]Backup created:[/] {Markup.Escape(path)}");
                AnsiConsole.MarkupLine($"  Documents: {manifest.DocumentCount}");
                AnsiConsole.MarkupLine($"  Size: {new FileInfo(path).Length / 1024}KB");
            }
        }
        finally { store.Dispose(); }
        return 0;
    }
}

public sealed class BackupRestoreCommand : AsyncCommand<BackupRestoreCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<FILE>")]
        public string File { get; set; } = "";

        [CommandOption("--force")]
        public bool Force { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (!settings.Force)
        {
            AnsiConsole.MarkupLine("[yellow]WARNING: Restore will overwrite existing data.[/]");
            if (!AnsiConsole.Confirm("Continue?"))
                return 0;
        }

        var config = ConfigManager.Load();
        var store = DocumentStoreFactory.CreateFromConfig(config);

        try
        {
            var svc = new BackupService(store);
            await svc.RestoreAsync(settings.File, cancellation);

            if (settings.Json)
                Console.WriteLine(JsonSerializer.Serialize(new { restored = true, file = settings.File }));
            else
                AnsiConsole.MarkupLine($"[green]Restored from:[/] {Markup.Escape(settings.File)}");
        }
        finally { store.Dispose(); }
        return 0;
    }
}

public sealed class BackupListCommand : AsyncCommand<BackupListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--dir <PATH>")]
        public string? Dir { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var backupDir = settings.Dir ?? BackupService.GetBackupDir(config.Backup);
        var backups = BackupService.ListBackups(backupDir);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(backups.Select(b => new { path = b.Path, modified = b.Modified, sizeKb = b.Size / 1024 }),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
            return Task.FromResult(0);
        }

        if (backups.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No backups found in[/] {Markup.Escape(backupDir)}");
            return Task.FromResult(0);
        }

        var table = new Table().Border(TableBorder.Simple)
            .AddColumn("File").AddColumn("Date").AddColumn("Size");

        foreach (var (path, modified, size) in backups)
            table.AddRow(Markup.Escape(Path.GetFileName(path)), modified.ToString("g"), $"{size / 1024}KB");

        AnsiConsole.Write(table);
        return Task.FromResult(0);
    }
}

public sealed class BackupPruneCommand : AsyncCommand<BackupPruneCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--keep <N>")]
        public int? Keep { get; set; }

        [CommandOption("--dry-run")]
        public bool DryRun { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var backupDir = BackupService.GetBackupDir(config.Backup);
        var retain = settings.Keep ?? config.Backup.RetainCount;

        if (settings.DryRun)
        {
            var backups = BackupService.ListBackups(backupDir);
            var toDelete = backups.Skip(retain).ToList();
            if (settings.Json)
                Console.WriteLine(JsonSerializer.Serialize(new { wouldDelete = toDelete.Count }));
            else
                AnsiConsole.MarkupLine($"Would delete {toDelete.Count} backup(s), keeping {retain}");
            return Task.FromResult(0);
        }

        var deleted = BackupService.PruneBackups(backupDir, retain);

        if (settings.Json)
            Console.WriteLine(JsonSerializer.Serialize(new { deleted, retained = retain }));
        else
            AnsiConsole.MarkupLine($"[green]Pruned:[/] {deleted} backup(s) deleted, {retain} retained");

        return Task.FromResult(0);
    }
}
