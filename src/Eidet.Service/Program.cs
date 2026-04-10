using Eidet.Core;
using Eidet.Service.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("eidet");
    config.SetApplicationVersion(EidetVersion.Current);

    config.AddCommand<SetupCommand>("setup")
        .WithDescription("First-time configuration wizard");

    config.AddCommand<McpCommand>("mcp")
        .WithDescription("Start MCP server (stdio transport for AI clients)");

    config.AddCommand<ServeCommand>("serve")
        .WithDescription("Start the Eidet REST API service");

    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Test connections and troubleshoot issues");

    config.AddCommand<StatusCommand>("status")
        .WithDescription("Show service status and stats");

    config.AddCommand<RecallCommand>("recall")
        .WithDescription("Search memories");

    config.AddCommand<StoreCommand>("store")
        .WithDescription("Store a memory");

    config.AddCommand<StatsCommand>("stats")
        .WithDescription("Memory counts by type");

    config.AddCommand<ExportCommand>("export")
        .WithDescription("Export memories as markdown");

    config.AddCommand<IntakeCommand>("intake")
        .WithDescription("Ingest project files as seed memories");

    config.AddCommand<MaintainCommand>("maintain")
        .WithDescription("Run maintenance pipeline");

    config.AddCommand<QualityCommand>("quality")
        .WithDescription("Analyze memory quality and detect issues");

    config.AddCommand<InstallCommand>("install")
        .WithDescription("Install Eidet as a system service");

    config.AddCommand<UninstallCommand>("uninstall")
        .WithDescription("Uninstall Eidet system service");

    config.AddCommand<InstructionsCommand>("instructions")
        .WithDescription("Generate CLAUDE.md memory usage instructions");

    config.AddCommand<DockerCommand>("docker")
        .WithDescription("Docker/devcontainer integration guide");

    config.AddCommand<UpdateCommand>("update")
        .WithDescription("Check for and install updates");

    config.AddBranch("backup", backup =>
    {
        backup.SetDescription("Backup and restore memory data");

        backup.AddCommand<BackupCreateCommand>("create")
            .WithDescription("Create a full backup");

        backup.AddCommand<BackupRestoreCommand>("restore")
            .WithDescription("Restore from a backup file");

        backup.AddCommand<BackupListCommand>("list")
            .WithDescription("List available backups");

        backup.AddCommand<BackupPruneCommand>("prune")
            .WithDescription("Delete old backups per retention policy");
    });

    config.AddBranch("api-key", apiKey =>
    {
        apiKey.SetDescription("Manage API keys for authentication");

        apiKey.AddCommand<ApiKeyCreateCommand>("create")
            .WithDescription("Create a new API key");

        apiKey.AddCommand<ApiKeyListCommand>("list")
            .WithDescription("List all API keys");

        apiKey.AddCommand<ApiKeyRevokeCommand>("revoke")
            .WithDescription("Revoke an API key");
    });

    config.AddBranch("config", cfg =>
    {
        cfg.SetDescription("View and modify configuration");

        cfg.AddCommand<ConfigGetCommand>("get")
            .WithDescription("Get a config value");

        cfg.AddCommand<ConfigSetCommand>("set")
            .WithDescription("Set a config value");

        cfg.AddCommand<ConfigListCommand>("list")
            .WithDescription("List all config values");
    });

    config.AddBranch("ollama", ollama =>
    {
        ollama.SetDescription("Manage Ollama models for enrichment");

        ollama.AddCommand<OllamaStatusCommand>("status")
            .WithDescription("Show Ollama connection and model status");

        ollama.AddCommand<OllamaPullCommand>("pull")
            .WithDescription("Pull/download an Ollama model");

        ollama.AddCommand<OllamaListCommand>("list")
            .WithDescription("List installed Ollama models");
    });
});

return app.Run(args);
