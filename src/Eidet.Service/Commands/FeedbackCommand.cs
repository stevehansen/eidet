using System.Diagnostics;
using System.Runtime.InteropServices;
using Eidet.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class FeedbackCommand : AsyncCommand<FeedbackCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var version = EidetVersion.Current;
        var os = RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.OSArchitecture.ToString();
        var runtime = RuntimeInformation.FrameworkDescription;

        var body = $"""
        **Eidet version:** {version}
        **OS:** {os} ({arch})
        **Runtime:** {runtime}

        ## Description

        <!-- Describe your feedback, bug report, or feature request here -->

        """;

        var encodedBody = Uri.EscapeDataString(body);
        var encodedTitle = Uri.EscapeDataString($"[Feedback] v{version}: ");
        var url = $"https://github.com/stevehansen/eidet/issues/new?title={encodedTitle}&body={encodedBody}&labels=feedback";

        if (settings.Json)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { url }));
            return Task.FromResult(0);
        }

        AnsiConsole.MarkupLine($"[bold]Eidet[/] v{version}");
        AnsiConsole.MarkupLine($"Opening GitHub Issues in your browser...");
        AnsiConsole.WriteLine();

        try
        {
            OpenBrowser(url);
        }
        catch
        {
            AnsiConsole.MarkupLine("[yellow]Could not open browser automatically.[/]");
            AnsiConsole.MarkupLine($"Open this URL manually:");
            AnsiConsole.MarkupLine($"  [link]{url}[/]");
        }

        return Task.FromResult(0);
    }

    private static void OpenBrowser(string url)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
        else
            Process.Start("xdg-open", url);
    }
}
