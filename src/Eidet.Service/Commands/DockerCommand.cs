using Eidet.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class DockerCommand : AsyncCommand<DockerCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var config = ConfigManager.Load();
        var inContainer = IsRunningInContainer();
        var port = config.Service.Port;

        if (settings.Json)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                inContainer,
                hostUrl = $"http://host.docker.internal:{port}",
                devcontainerJson = GetDevcontainerSnippet(port),
                dockerfileSnippet = GetDockerfileSnippet(),
                mcpConfig = GetMcpConfig(),
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            return Task.FromResult(0);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Eidet Docker Integration Guide[/]");
        AnsiConsole.WriteLine();

        if (inContainer)
        {
            AnsiConsole.MarkupLine("[yellow]Running inside a container.[/]");
            AnsiConsole.MarkupLine($"  Eidet host service should be accessible at:");
            AnsiConsole.MarkupLine($"  [dim]http://host.docker.internal:{port}[/]");
            AnsiConsole.WriteLine();
        }
        else
        {
            AnsiConsole.MarkupLine($"[green]Running on host.[/] Service port: {port}");
            AnsiConsole.WriteLine();
        }

        // devcontainer.json
        AnsiConsole.MarkupLine("[bold]1. DevContainer Configuration[/]");
        AnsiConsole.MarkupLine("Add to your [dim]devcontainer.json[/]:");
        AnsiConsole.WriteLine();

        var panel = new Panel(GetDevcontainerSnippet(port))
            .Header("devcontainer.json")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();

        // Dockerfile
        AnsiConsole.MarkupLine("[bold]2. Install Eidet CLI in Container[/]");
        AnsiConsole.MarkupLine("Add to your [dim]Dockerfile[/] (optional — for MCP stdio):");
        AnsiConsole.WriteLine();

        var dockerPanel = new Panel(GetDockerfileSnippet())
            .Header("Dockerfile")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
        AnsiConsole.Write(dockerPanel);
        AnsiConsole.WriteLine();

        // MCP config
        AnsiConsole.MarkupLine("[bold]3. MCP Configuration (inside container)[/]");
        AnsiConsole.MarkupLine("If using MCP inside the container, set the environment:");
        AnsiConsole.WriteLine();

        var mcpPanel = new Panel(GetMcpConfig())
            .Header(".mcp.json")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Grey);
        AnsiConsole.Write(mcpPanel);
        AnsiConsole.WriteLine();

        // Environment variable
        AnsiConsole.MarkupLine("[bold]4. Environment Variable Override[/]");
        AnsiConsole.MarkupLine($"  Set [dim]EIDET_API_URL[/] to override the default API URL.");
        AnsiConsole.MarkupLine($"  Example: [dim]EIDET_API_URL=http://host.docker.internal:{port}[/]");
        AnsiConsole.WriteLine();

        return Task.FromResult(0);
    }

    internal static bool IsRunningInContainer()
    {
        // Check common container indicators
        if (File.Exists("/.dockerenv")) return true;
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true") return true;
        if (Environment.GetEnvironmentVariable("container") != null) return true;

        try
        {
            if (File.Exists("/proc/1/cgroup"))
            {
                var cgroup = File.ReadAllText("/proc/1/cgroup");
                if (cgroup.Contains("docker") || cgroup.Contains("containerd") || cgroup.Contains("kubepods"))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static string GetDevcontainerSnippet(int port) =>
        $$"""
        {
          "forwardPorts": [{{port}}],
          "remoteEnv": {
            "EIDET_API_URL": "http://host.docker.internal:{{port}}"
          },
          "postCreateCommand": "echo 'Eidet available at http://host.docker.internal:{{port}}'"
        }
        """;

    private static string GetDockerfileSnippet() =>
        """
        # Install Eidet CLI (for MCP stdio bridge inside container)
        # Download from GitHub releases or copy from host
        COPY --from=host /path/to/eidet /usr/local/bin/eidet
        # Or use a multi-stage build:
        # RUN dotnet tool install --global eidet
        """;

    private static string GetMcpConfig() =>
        """
        {
          "mcpServers": {
            "eidet": {
              "command": "eidet",
              "args": ["mcp"],
              "env": {
                "EIDET_API_URL": "http://host.docker.internal:19380"
              }
            }
          }
        }
        """;
}
