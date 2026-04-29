using System.Text;
using Eidet.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class LogsCommand : AsyncCommand<LogsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-n|--tail <LINES>")]
        [System.ComponentModel.DefaultValue(100)]
        public int Tail { get; set; }

        [CommandOption("-f|--follow")]
        public bool Follow { get; set; }

        [CommandOption("--path")]
        public bool ShowPath { get; set; }

        [CommandOption("--errors")]
        public bool ErrorsOnly { get; set; }

        [CommandOption("--no-color")]
        public bool NoColor { get; set; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var path = EidetLog.LogPath;

        if (settings.ShowPath)
        {
            AnsiConsole.WriteLine(path);
            return 0;
        }

        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[yellow]No log file yet[/] ({path})");
            return 0;
        }

        var initial = ReadTail(path, settings.Tail);
        long position;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            position = fs.Length;
        }

        foreach (var line in initial)
        {
            if (settings.ErrorsOnly && !line.Contains("[ERR]") && !line.Contains("[WRN]"))
                continue;
            WriteLine(line, settings.NoColor);
        }

        if (!settings.Follow) return 0;

        AnsiConsole.MarkupLine("[dim]-- following (Ctrl+C to stop) --[/]");

        var lastSize = position;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(path))
                {
                    await Task.Delay(500, cancellation);
                    continue;
                }

                var info = new FileInfo(path);
                if (info.Length < lastSize)
                {
                    // Rotated — restart from beginning
                    lastSize = 0;
                }

                if (info.Length > lastSize)
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    fs.Seek(lastSize, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    string? line;
                    while ((line = await reader.ReadLineAsync(cancellation)) != null)
                    {
                        if (settings.ErrorsOnly && !line.Contains("[ERR]") && !line.Contains("[WRN]"))
                            continue;
                        WriteLine(line, settings.NoColor);
                    }
                    lastSize = fs.Position;
                }
            }
            catch (IOException)
            {
                // Transient — log may be rotating; retry
            }

            await Task.Delay(500, cancellation);
        }

        return 0;
    }

    private static void WriteLine(string line, bool noColor)
    {
        if (noColor)
        {
            Console.WriteLine(line);
            return;
        }

        var color =
            line.Contains("[ERR]") ? "red" :
            line.Contains("[WRN]") ? "yellow" :
            line.Contains("[INF]") ? "grey70" :
            null;

        var safe = Markup.Escape(line);
        if (color is null)
            AnsiConsole.WriteLine(line);
        else
            AnsiConsole.MarkupLine($"[{color}]{safe}[/]");
    }

    private static List<string> ReadTail(string path, int tail)
    {
        if (tail <= 0) tail = 100;

        var lines = new LinkedList<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lines.AddLast(line);
            if (lines.Count > tail) lines.RemoveFirst();
        }
        return lines.ToList();
    }
}
