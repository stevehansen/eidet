using Eidet.Bench;
using Eidet.Benchmark.Tests;
using Eidet.Core.Services;

namespace Eidet.Bench.Tests;

/// <summary>
/// The deterministic script behind the committed fixture transcript, plus the arm compositions
/// every logic test shares. The scripted solver produces the task's canonical patch only when
/// the recalled context contains that task's trigger keyword (related tasks always succeed —
/// they exist to produce trajectories); the scripted oracle resolves exactly the canonical patch.
/// Keywords are chosen so a base task succeeds iff the matching related trajectory was recalled:
/// 201 needs "clamped" (in 101's trajectory), 202 needs "transliterat" (in 102's), and 203 needs
/// "checkpoint" (in no trajectory — it stays unresolved in every arm, keeping the fixture honest
/// about failures).
/// </summary>
internal static class FixtureScript
{
    public const string RepoId = "swe-context-bench-fixture";

    private const long RelatedTokens = 900;
    private const long GoodTokens = 1400;
    private const long BadTokens = 700;

    private static readonly Dictionary<string, string?> TriggerKeywords = new()
    {
        ["acme__parquet-tools-101"] = null, // related — always solved
        ["acme__parquet-tools-102"] = null, // related — always solved
        ["acme__parquet-tools-201"] = "clamped",
        ["acme__parquet-tools-202"] = "transliterat",
        ["acme__parquet-tools-203"] = "checkpoint",
    };

    public static async Task<(ISolverPort Solver, IOraclePort Oracle)> ScriptedPortsAsync()
    {
        var tasks = await new FixtureDataset().LoadAsync(0);
        var canonicalPatches = tasks.ToDictionary(t => t.InstanceId, t => t.Patch);
        return (new ScriptedSolver(canonicalPatches), new ScriptedOracle(canonicalPatches));
    }

    /// <summary>The Eidet arm: real MemoryService pipeline over the in-memory test store.</summary>
    public static IMemoryBackend NewEidetArm()
    {
        var store = new BenchInMemoryStore();
        var service = new MemoryService(store, new LayerService(store));
        return new InProcessEidetBackend(service, store, RepoId);
    }

    public static SweBenchHarness NewHarness(
        IMemoryBackend memory, ISolverPort solver, IOraclePort oracle,
        IReadOnlyList<ICapabilityScorer>? scorers = null) =>
        new(new FixtureDataset(), memory, solver, oracle, scorers ?? [], TimeProvider.System);

    /// <summary>
    /// Records both fixture arms (no-memory control + in-process Eidet) into one transcript —
    /// the exact recipe the committed <c>fixture-transcript.json</c> is generated from.
    /// </summary>
    public static async Task<Transcript> RecordBothArmsAsync()
    {
        var transcript = new Transcript();
        var (solver, oracle) = await ScriptedPortsAsync();
        var recordingSolver = new RecordingSolver(solver, transcript);
        var recordingOracle = new RecordingOracle(oracle, transcript);

        await NewHarness(new NoMemoryBackend(), recordingSolver, recordingOracle).RunAsync(0);
        await NewHarness(NewEidetArm(), recordingSolver, recordingOracle).RunAsync(0);
        return transcript;
    }

    private sealed class ScriptedSolver(Dictionary<string, string> canonicalPatches) : ISolverPort
    {
        public bool IsAvailable => true;

        public Task<SolveResult> AttemptAsync(SolveRequest request, CancellationToken ct = default)
        {
            var keyword = TriggerKeywords[request.InstanceId];
            var isRelated = keyword is null;
            var solved = isRelated ||
                         request.Context.Any(c => c.Contains(keyword!, StringComparison.OrdinalIgnoreCase));
            var result = solved
                ? new SolveResult(canonicalPatches[request.InstanceId], isRelated ? RelatedTokens : GoodTokens)
                : new SolveResult($"diff --git a/guess.py b/guess.py\n+# speculative fix for {request.InstanceId}\n", BadTokens);
            return Task.FromResult(result);
        }

        public Task<bool> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class ScriptedOracle(Dictionary<string, string> canonicalPatches) : IOraclePort
    {
        public Task<Verdict> ResolveAsync(SweTask task, string patch, CancellationToken ct = default)
        {
            var isCanonical = patch == canonicalPatches[task.InstanceId];
            // A wrong patch fails FAIL_TO_PASS but doesn't regress PASS_TO_PASS.
            return Task.FromResult(new Verdict(FailToPassPassed: isCanonical, PassToPassPassed: true));
        }
    }
}
