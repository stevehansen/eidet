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
            var dataDir = config.Storage.DataDir ?? DocumentStoreFactory.GetDefaultDataDir();
            config.Storage.DataDir = dataDir;

            AnsiConsole.MarkupLine($"  Storage:  [green]Embedded mode[/]");
            AnsiConsole.MarkupLine($"  Data dir: {Markup.Escape(dataDir)}");

            // Start embedded server and provision
            Raven.Client.Documents.IDocumentStore? store = null;
            try
            {
                store = DocumentStoreFactory.CreateEmbedded(dataDir, config.Storage.DatabaseName);

                AnsiConsole.MarkupLine("  Deploying indexes...");
                DatabaseProvisioner.DeployIndexes(store);
                AnsiConsole.MarkupLine("  [green]✓[/] Indexes deployed (Memories/Search, Memories/CountByType)");

                AnsiConsole.MarkupLine("  Configuring embeddings (bge-micro-v2)...");
                var embeddingsError = DatabaseProvisioner.EnsureEmbeddingsConfigured(store);
                if (embeddingsError == null)
                    AnsiConsole.MarkupLine("  [green]✓[/] Embeddings configured");
                else
                    AnsiConsole.MarkupLine($"  [yellow]~[/] Embeddings: {embeddingsError}");

                AnsiConsole.MarkupLine("  [green]✓[/] Embedded RavenDB configured");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] Embedded setup failed: {ex.Message}");
                store?.Dispose();
                return 1;
            }
            finally
            {
                store?.Dispose();
            }

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

        // ── Step 2: Enrichment backend (optional) ────────────────────────

        AnsiConsole.WriteLine();
        var backends = await EnrichmentDetector.DetectAsync(config.Enrichment, cancellation);

        if (backends.Count == 0)
        {
            AnsiConsole.MarkupLine("  [dim]No enrichment backend found (Ollama or LM Studio — optional).[/]");
            AnsiConsole.MarkupLine("  [dim]Configure later with: eidet enrichment setup[/]");
        }
        else
        {
            foreach (var d in backends)
                AnsiConsole.MarkupLine($"  [green]✓[/] {Markup.Escape(d.Describe())} detected");

            // Single hit configures itself; a tie asks. Non-interactive keeps
            // DetectAsync's Ollama-first ordering — the pre-detection default.
            var backend = backends[0];
            if (backends.Count > 1 && !settings.NonInteractive)
            {
                var descriptions = backends.Select(d => d.Describe()).ToList();
                var picked = AnsiConsole.Prompt(new SelectionPrompt<string>()
                    .Title("  Multiple backends found — which one should Eidet use?")
                    .AddChoices(descriptions));
                backend = backends[descriptions.IndexOf(picked)];
            }

            var enable = settings.NonInteractive
                || AnsiConsole.Confirm($"  Enable {backend.Label} enrichment?", defaultValue: true);
            if (enable)
            {
                config.Enrichment.Enabled = true;
                config.Enrichment.Provider = backend.Provider;
                config.Enrichment.Url = backend.Url;
                config.Enrichment.ApiKey = backend.ApiKey;

                // Ollama keeps the configured model (pullable later); an OpenAI-compatible
                // server only works with a model id from its own list.
                if (backend.Provider == EnrichmentProvider.OpenAiCompatible && backend.Models.Count > 0
                    && !backend.Models.Contains(config.Enrichment.Model, StringComparer.OrdinalIgnoreCase))
                {
                    config.Enrichment.Model = settings.NonInteractive || backend.Models.Count == 1
                        ? backend.Models[0]
                        : AnsiConsole.Prompt(new SelectionPrompt<string>()
                            .Title("  Model:").PageSize(15).AddChoices(backend.Models));
                    AnsiConsole.MarkupLine($"  Model: {Markup.Escape(config.Enrichment.Model)}");
                }
            }
            else
            {
                config.Enrichment.Enabled = false;
            }
            changed = true;
        }

        // ── Step 3: Automatic updates ────────────────────────────────────
        // Asked rather than defaulted: a tool that replaces its own binary overnight should be
        // something the user agreed to, not something they discover from a changed version number.

        if (!settings.NonInteractive && !config.Update.AutoUpdate)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Automatic updates[/]");
            AnsiConsole.MarkupLine($"  [dim]Installs new releases at {Markup.Escape(config.Update.AtLocalTime)} local time, " +
                                   $"skipping anything published in the last {config.Update.MinimumAgeHours}h.[/]");
            AnsiConsole.MarkupLine("  [dim]Either way you'll be told when a new version exists.[/]");

            if (AnsiConsole.Confirm("  Install updates automatically?", defaultValue: true))
            {
                config.Update.AutoUpdate = true;
                changed = true;
            }
        }

        // ── Step 4: Save config ──────────────────────────────────────────

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
