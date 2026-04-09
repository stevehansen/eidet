using Eidet.Core;
using Eidet.Core.Configuration;
using Eidet.Core.Storage;
using Raven.Client.Documents;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class SetupCommand : AsyncCommand<SetupCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--non-interactive")]
        public bool NonInteractive { get; set; }

        [CommandOption("--raven-url <URL>")]
        public string? RavenUrl { get; set; }

        [CommandOption("--database <NAME>")]
        public string? DatabaseName { get; set; }

        [CommandOption("--embedded")]
        public bool Embedded { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        AnsiConsole.MarkupLine($"[bold]Eidet[/] v{EidetVersion.Current} — Setup");
        AnsiConsole.WriteLine();

        var config = ConfigManager.Load();
        var changed = false;

        // ── Step 1: RavenDB ──────────────────────────────────────────────

        if (settings.Embedded)
        {
            config.Storage.Mode = StorageMode.Embedded;
            AnsiConsole.MarkupLine("  Storage:  [yellow]Embedded mode[/] (not yet implemented)");
            changed = true;
        }
        else
        {
            var ravenUrl = settings.RavenUrl ?? config.Storage.RavenUrl;

            if (!settings.NonInteractive && settings.RavenUrl == null)
            {
                AnsiConsole.MarkupLine("  [bold]Checking for RavenDB...[/]");

                var detected = await TryConnectRaven(ravenUrl);
                if (detected != null)
                {
                    AnsiConsole.MarkupLine($"  [green]✓[/] Found RavenDB at {ravenUrl} (v{detected})");
                    var useIt = AnsiConsole.Confirm("  Use this RavenDB instance?", defaultValue: true);
                    if (!useIt)
                    {
                        ravenUrl = AnsiConsole.Ask<string>("  RavenDB URL:", ravenUrl);
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine($"  [red]✗[/] No RavenDB found at {ravenUrl}");
                    ravenUrl = AnsiConsole.Ask<string>("  RavenDB URL:", ravenUrl);
                }
            }

            config.Storage.Mode = StorageMode.External;
            config.Storage.RavenUrl = ravenUrl;
            changed = true;

            // Connect and provision
            IDocumentStore? store = null;
            try
            {
                var dbName = settings.DatabaseName ?? config.Storage.DatabaseName;
                config.Storage.DatabaseName = dbName;

                store = DocumentStoreFactory.Create(ravenUrl, dbName);

                // Create database
                if (!DatabaseProvisioner.DatabaseExists(store))
                {
                    AnsiConsole.MarkupLine($"  Creating database \"{dbName}\"...");
                    DatabaseProvisioner.EnsureDatabaseExists(store);
                    AnsiConsole.MarkupLine($"  [green]✓[/] Database \"{dbName}\" created");
                }
                else
                {
                    AnsiConsole.MarkupLine($"  [green]✓[/] Database \"{dbName}\" exists");
                }

                // Deploy indexes
                AnsiConsole.MarkupLine("  Deploying indexes...");
                DatabaseProvisioner.DeployIndexes(store);
                AnsiConsole.MarkupLine("  [green]✓[/] Indexes deployed (Memories/Search, Memories/CountByType)");

                // Configure embeddings
                AnsiConsole.MarkupLine("  Configuring embeddings (bge-micro-v2)...");
                var embeddingsError = DatabaseProvisioner.EnsureEmbeddingsConfigured(store);
                if (embeddingsError == null)
                    AnsiConsole.MarkupLine("  [green]✓[/] Embeddings configured");
                else
                    AnsiConsole.MarkupLine($"  [yellow]~[/] Embeddings: {embeddingsError}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] RavenDB setup failed: {ex.Message}");
                store?.Dispose();
                return 1;
            }
            finally
            {
                store?.Dispose();
            }
        }

        // ── Step 2: Ollama (optional) ────────────────────────────────────

        AnsiConsole.WriteLine();
        var ollamaConnected = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync($"{config.Enrichment.OllamaUrl}/api/version", cancellation);
            ollamaConnected = resp.IsSuccessStatusCode;
        }
        catch { }

        if (ollamaConnected)
        {
            AnsiConsole.MarkupLine($"  [green]✓[/] Ollama detected at {config.Enrichment.OllamaUrl}");
            if (!settings.NonInteractive)
            {
                config.Enrichment.OllamaEnabled = AnsiConsole.Confirm("  Enable Ollama enrichment?", defaultValue: true);
            }
            else
            {
                config.Enrichment.OllamaEnabled = true;
            }
            changed = true;
        }
        else
        {
            AnsiConsole.MarkupLine("  [dim]Ollama not found (enrichment disabled — optional)[/]");
        }

        // ── Step 3: Save config ──────────────────────────────────────────

        if (changed)
        {
            AnsiConsole.WriteLine();
            ConfigManager.Save(config);
            AnsiConsole.MarkupLine($"  [green]✓[/] Config saved to {ConfigManager.GetConfigPath()}");
        }

        // ── Summary ──────────────────────────────────────────────────────

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold green]Setup complete![/]");
        AnsiConsole.MarkupLine($"  Start the service:  [dim]eidet serve[/]");
        AnsiConsole.MarkupLine($"  Check health:       [dim]eidet doctor[/]");
        AnsiConsole.WriteLine();

        return 0;
    }

    private static async Task<string?> TryConnectRaven(string url)
    {
        try
        {
            using var store = DocumentStoreFactory.Create(url, "_");
            var buildInfo = await store.Maintenance.Server.SendAsync(
                new Raven.Client.ServerWide.Operations.GetBuildNumberOperation());
            return buildInfo.FullVersion;
        }
        catch
        {
            return null;
        }
    }
}
