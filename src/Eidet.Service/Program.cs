using Eidet.Core;
using Eidet.Service.Commands;
using Spectre.Console.Cli;

// Translate "eidet help <command>" to "eidet <command> --help"
if (args.Length >= 1 && string.Equals(args[0], "help", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length == 1)
    {
        args = ["--help"];
    }
    else
    {
        // "help recall" → "recall --help"
        var newArgs = new string[args.Length];
        Array.Copy(args, 1, newArgs, 0, args.Length - 1);
        newArgs[^1] = "--help";
        args = newArgs;
    }
}

// "eidet mcp install [...]" / "eidet mcp list" — route to dedicated commands.
// (The bare `eidet mcp` stays as the MCP server entry point, so we can't add
// these as branch subcommands without breaking existing MCP client configs.)
if (args.Length >= 2 && string.Equals(args[0], "mcp", StringComparison.OrdinalIgnoreCase)
    && args[1] is "install" or "list" or "uninstall")
{
    var sub = args[1];
    var rewritten = new string[args.Length - 1];
    rewritten[0] = $"_mcp-{sub}";
    Array.Copy(args, 2, rewritten, 1, args.Length - 2);
    args = rewritten;
}

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("eidet");
    config.SetApplicationVersion(EidetVersion.Current);

    config.AddCommand<SetupCommand>("setup")
        .WithDescription("First-time configuration wizard");

    config.AddCommand<McpCommand>("mcp")
        .WithDescription("Start MCP server (stdio transport for AI clients). " +
                         "Use `eidet mcp install <client>` to register with a client, " +
                         "`eidet mcp list` to see registration status.");

    config.AddCommand<McpInstallCommand>("_mcp-install")
        .WithDescription("Register eidet as an MCP server in an AI client (`eidet mcp install [client]`)")
        .IsHidden();

    config.AddCommand<McpListCommand>("_mcp-list")
        .WithDescription("Show MCP client registration status (`eidet mcp list`)")
        .IsHidden();

    config.AddCommand<ServeCommand>("serve")
        .WithDescription("Start the Eidet REST API service");

    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Test connections and troubleshoot issues");

    config.AddCommand<StatusCommand>("status")
        .WithDescription("Show service status and stats");

    config.AddCommand<LogsCommand>("logs")
        .WithDescription("Print or follow the eidet.log file");

    config.AddCommand<RecallCommand>("recall")
        .WithDescription("Search memories");

    config.AddCommand<ContextCommand>("context")
        .WithDescription("Preview the L0+L1 context sent to AI agents");

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

    config.AddCommand<FeedbackCommand>("feedback")
        .WithDescription("Report an issue or give feedback on GitHub");

    config.AddBranch("layer", layer =>
    {
        layer.SetDescription("Manage memory layers");

        layer.AddCommand<LayerSyncCommand>("sync")
            .WithDescription("Sync a .eidet pack file into a layer (add/update/remove)");

        layer.AddCommand<LayerListCommand>("list")
            .WithDescription("List mounted layers for a repo");
    });

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

    config.AddBranch("bench", bench =>
    {
        bench.SetDescription("SWE Context Bench harness (offline fixture smoke by default)");
        bench.SetDefaultCommand<BenchSmokeCommand>();

        bench.AddCommand<BenchFullCommand>("full")
            .WithDescription("Run against the real SWE Context Bench dataset (refuses to emit a number without it)");
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
