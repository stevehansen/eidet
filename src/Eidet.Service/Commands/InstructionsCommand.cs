using Eidet.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

public sealed class InstructionsCommand : AsyncCommand<InstructionsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--install")]
        public bool Install { get; set; }

        [CommandOption("--project")]
        public bool Project { get; set; }

        [CommandOption("--print")]
        public bool Print { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var instructions = GenerateInstructions();

        if (settings.Print || (!settings.Install && !settings.Project))
        {
            Console.WriteLine(instructions);
            return Task.FromResult(0);
        }

        if (settings.Install)
        {
            var claudeMdPath = GetGlobalClaudeMdPath();
            if (claudeMdPath == null)
            {
                AnsiConsole.MarkupLine("[red]Could not determine Claude Code config directory.[/]");
                return Task.FromResult(1);
            }

            return Task.FromResult(AppendToFile(claudeMdPath, instructions, "global"));
        }

        if (settings.Project)
        {
            var projectPath = Path.Combine(Directory.GetCurrentDirectory(), "CLAUDE.md");
            return Task.FromResult(AppendToFile(projectPath, instructions, "project"));
        }

        return Task.FromResult(0);
    }

    private static int AppendToFile(string path, string instructions, string scope)
    {
        var marker = "<!-- eidet-instructions -->";
        var endMarker = "<!-- /eidet-instructions -->";

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            if (existing.Contains(marker))
            {
                // Replace existing section
                var start = existing.IndexOf(marker);
                var end = existing.IndexOf(endMarker);
                if (end > start)
                {
                    var before = existing[..start];
                    var after = existing[(end + endMarker.Length)..];
                    File.WriteAllText(path, before + marker + "\n" + instructions + "\n" + endMarker + after);
                    AnsiConsole.MarkupLine($"[green]Updated[/] Eidet instructions in {Markup.Escape(path)}");
                    return 0;
                }
            }

            // Append
            File.AppendAllText(path, "\n\n" + marker + "\n" + instructions + "\n" + endMarker + "\n");
            AnsiConsole.MarkupLine($"[green]Appended[/] Eidet instructions to {Markup.Escape(path)}");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, marker + "\n" + instructions + "\n" + endMarker + "\n");
            AnsiConsole.MarkupLine($"[green]Created[/] {Markup.Escape(path)} with Eidet instructions");
        }

        return 0;
    }

    internal static string? GetGlobalClaudeMdPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", "CLAUDE.md");
    }

    internal static string GenerateInstructions()
    {
        return $"""
            ## Memory System (Eidet v{EidetVersion.Current})

            This project uses Eidet for persistent AI memory across sessions.
            Memory tools are available via MCP (`eidet mcp`).

            ### Session Start
            - Call `eidet_context` to load project knowledge (~600 tokens of L0 identity + L1 top memories)
            - Call `eidet_recall` with a query relevant to the current task for deeper context

            ### During Work
            - **Store observations** (`eidet_store` type=observation) for non-obvious discoveries:
              - Bug root causes, tricky configurations, "gotchas"
              - Architecture decisions and their rationale
              - Important relationships between components
            - **Store insights** (`eidet_store` type=insight) for confirmed, stable knowledge:
              - Patterns that work well in this codebase
              - Integration points, API contracts, deployment notes
            - **Store procedures** (`eidet_store` type=procedure) for multi-step workflows:
              - How to set up/test/deploy specific features
              - Debugging recipes for recurring issues
            - **Store heuristics** (`eidet_store` type=heuristic) for rules of thumb:
              - "Always run migrations before tests in this repo"
              - "The CI flakes on Windows when path > 260 chars"
            - Do NOT store things easily derived from code or git history
            - Include relevant tags for discoverability (e.g., "testing", "deployment", "api")
            - Set importance: 0.3=minor, 0.5=normal, 0.8=important, 1.0=critical

            ### Before compaction / context clear
            - When you receive a context-eviction or compaction warning, call `eidet_store` for any durable finding not yet saved (bug root causes, decisions, gotchas).
            - Memory in Eidet survives compaction; in-context notes do not.
            - After a restart or compaction, call `eidet_context` (and `eidet_recall` for the task) to reload what you saved.

            ### Feedback
            - Call `eidet_feedback` with `used: true` (echo) when a recalled memory was helpful
            - Call `eidet_feedback` with `used: false` (fizzle) when a recalled memory was irrelevant
            - This feedback tunes future recall scoring

            ### Forgetting
            - Call `eidet_forget` to invalidate memories that are no longer true (with a reason)

            ### Available Tools
            | Tool | Purpose |
            |------|---------|
            | `eidet_context` | Get compact project context (~600 tokens) |
            | `eidet_recall` | Search memories (hybrid vector + full-text) |
            | `eidet_store` | Store a memory (observation/insight/procedure/heuristic) |
            | `eidet_feedback` | Echo (useful) or fizzle (irrelevant) a memory |
            | `eidet_forget` | Soft-delete a memory with audit trail |
            | `eidet_link` | Create cross-repo or memory-to-memory links |
            """;
    }
}
