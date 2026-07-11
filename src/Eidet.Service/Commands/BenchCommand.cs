using System.Globalization;
using Eidet.Bench;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eidet.Service.Commands;

/// <summary>
/// `eidet bench` — the offline logic/smoke path: replays the bundled fixture transcript through
/// the harness with the no-memory control arm. Always works offline, never touches the user's
/// memory store, and never emits a leaderboard-shaped number (the guard note is printed with
/// every run).
/// </summary>
public sealed class BenchSmokeCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellation)
    {
        var dataset = new FixtureDataset();
        var transcript = Transcript.LoadEmbeddedFixture();
        var harness = new SweBenchHarness(
            dataset,
            new NoMemoryBackend(),
            new ReplaySolver(transcript),
            new ReplayOracle(transcript),
            scorers: [],
            TimeProvider.System);

        var report = await harness.RunAsync(limit: 0, cancellation);

        AnsiConsole.MarkupLine("[bold]SWE Context Bench — offline fixture smoke[/]");
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Simple)
            .AddColumn("Metric")
            .AddColumn("Value");
        var ci = CultureInfo.InvariantCulture;
        table.AddRow("Dataset", report.DatasetName);
        table.AddRow("Memory backend", $"{report.BackendName} (control arm)");
        table.AddRow("Related tasks (ingested)", report.RelatedTasks.ToString(ci));
        table.AddRow("Base tasks (evaluated)", report.BaseTasks.ToString(ci));
        table.AddRow("Resolved", report.Resolved.ToString(ci));
        table.AddRow("Resolution rate", report.ResolutionRate.ToString("F3", ci));
        table.AddRow("Solver tokens per resolved", report.Resolved == 0 ? "n/a" : report.TokensPerResolved.ToString(ci));
        table.AddRow("Runtime", $"{report.Runtime.TotalMilliseconds.ToString("F0", ci)} ms");
        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(LeaderboardGuard.Refusal(dataset))}[/]");
        return 0;
    }
}

/// <summary>
/// `eidet bench full` — the real, paid run. Phase 0 of issue #36 ships no real dataset/solver
/// adapters, so this always refuses; the refusal (rather than a fixture-derived figure) is the
/// anti-misreporting guard.
/// </summary>
public sealed class BenchFullCommand : AsyncCommand<BenchFullCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--dataset <PATH>")]
        public string? DatasetPath { get; set; }

        [CommandOption("--record <FILE>")]
        public string? RecordPath { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        var dataset = new PendingRealDataset(settings.DatasetPath);
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(LeaderboardGuard.Refusal(dataset, settings.DatasetPath))}[/]");

        if (settings.DatasetPath is not null &&
            (Directory.Exists(settings.DatasetPath) || File.Exists(settings.DatasetPath)))
        {
            AnsiConsole.MarkupLine(
                "[yellow]Dataset files found, but the real dataset adapter ships with Phase 1 of issue #36 —" +
                " no number can honestly be produced yet.[/]");
        }

        return Task.FromResult(1);
    }

    /// <summary>
    /// The Phase 1 seam: real provenance, never available until the parquet adapter exists.
    /// Keeping it a real <see cref="ISweDatasetPort"/> routes the refusal through the same
    /// <see cref="LeaderboardGuard"/> the tests pin down.
    /// </summary>
    private sealed class PendingRealDataset(string? path) : ISweDatasetPort
    {
        public string Name => "SWEContextBench";
        public bool IsRealDataset => true;
        public bool IsAvailable => false;
        public Task<IReadOnlyList<SweTask>> LoadAsync(int limit, CancellationToken ct = default) =>
            throw new NotSupportedException(
                $"The real dataset adapter ({path ?? "no path given"}) is Phase 1 of issue #36.");
    }
}
