using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eidet.Bench;

/// <summary>
/// The record/replay cache that makes a paid, non-deterministic benchmark run reproducible for
/// free: recording decorators write solver results and oracle verdicts during the real run; the
/// replay adapters re-derive the identical report offline from the committed file.
///
/// File format (<c>eidet-bench-transcript/v1</c>, self-describing JSON, stable bytes — sorted
/// keys, LF newlines):
/// <code>
/// {
///   "format": "eidet-bench-transcript/v1",
///   "solves":   { "&lt;sha256 of canonical SolveRequest JSON&gt;": { "instanceId", "patch", "tokensUsed" } },
///   "verdicts": { "&lt;sha256 of instanceId + '\n' + patch&gt;":   { "instanceId", "failToPassPassed", "passToPassPassed" } }
/// }
/// </code>
/// Solves are keyed on the full <see cref="SolveRequest"/> (context included) because the recalled
/// context changes what the solver sees; verdicts are keyed on (task, patch) because resolution
/// depends only on the patch. Recorder and replayer share the single serialization path in
/// <see cref="KeyForSolve"/> — re-record whenever a solver or oracle adapter changes.
///
/// Cross-machine reproducibility additionally assumes recall yields a <em>strict</em> fragment
/// ordering: the context list is serialized in order, and the final ranking sort
/// (<c>RecallScoring</c>) is unstable, so an exact score tie between two fragments could reorder
/// them — and thus the recorded key — arbitrarily across platforms. The fixture is engineered so
/// its trajectory texts never tie on any base query; a real dataset must preserve the same
/// property (or the ranking must break ties deterministically) for replay to stay byte-stable.
/// </summary>
public sealed class Transcript
{
    public const string Format = "eidet-bench-transcript/v1";

    private static readonly JsonSerializerOptions CanonicalKeyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions FileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
    };

    private readonly SortedDictionary<string, SolveEntry> _solves = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, VerdictEntry> _verdicts = new(StringComparer.Ordinal);

    public sealed record SolveEntry(string InstanceId, string Patch, long TokensUsed);
    public sealed record VerdictEntry(string InstanceId, bool FailToPassPassed, bool PassToPassPassed);

    /// <summary>The single serialization path both the recorder and the replayer key on.</summary>
    public static string KeyForSolve(SolveRequest request) =>
        Sha256(JsonSerializer.Serialize(request, CanonicalKeyJson));

    public static string KeyForVerdict(string instanceId, string patch) =>
        Sha256(instanceId + "\n" + patch);

    /// <summary>
    /// Records a solve. Re-recording the same request with a different result throws — that is
    /// either a non-deterministic solver or two adapter versions writing one transcript, and both
    /// invalidate replayed numbers.
    /// </summary>
    public void RecordSolve(SolveRequest request, SolveResult result)
    {
        var entry = new SolveEntry(request.InstanceId, result.Patch, result.TokensUsed);
        var key = KeyForSolve(request);
        if (_solves.TryGetValue(key, out var existing) && existing != entry)
            throw new InvalidOperationException(
                $"Conflicting recording for solve request {key} (task {request.InstanceId}) — " +
                "the solver is non-deterministic or the transcript mixes adapter versions. Re-record from scratch.");
        _solves[key] = entry;
    }

    public SolveResult? FindSolve(SolveRequest request) =>
        _solves.TryGetValue(KeyForSolve(request), out var e) ? new SolveResult(e.Patch, e.TokensUsed) : null;

    public void RecordVerdict(SweTask task, string patch, Verdict verdict)
    {
        var entry = new VerdictEntry(task.InstanceId, verdict.FailToPassPassed, verdict.PassToPassPassed);
        var key = KeyForVerdict(task.InstanceId, patch);
        if (_verdicts.TryGetValue(key, out var existing) && existing != entry)
            throw new InvalidOperationException(
                $"Conflicting recording for verdict {key} (task {task.InstanceId}) — " +
                "the oracle is non-deterministic or the transcript mixes adapter versions. Re-record from scratch.");
        _verdicts[key] = entry;
    }

    public Verdict? FindVerdict(SweTask task, string patch) =>
        _verdicts.TryGetValue(KeyForVerdict(task.InstanceId, patch), out var e)
            ? new Verdict(e.FailToPassPassed, e.PassToPassPassed)
            : null;

    public string ToJson() =>
        JsonSerializer.Serialize(new TranscriptFile(Format, _solves, _verdicts), FileJson) + "\n";

    public static Transcript FromJson(string json)
    {
        var file = JsonSerializer.Deserialize<TranscriptFile>(json, FileJson)
                   ?? throw new InvalidOperationException("Transcript JSON deserialized to null.");
        if (file.Format != Format)
            throw new InvalidOperationException(
                $"Unsupported transcript format '{file.Format}' (expected '{Format}').");

        var transcript = new Transcript();
        foreach (var (key, entry) in file.Solves)
            transcript._solves[key] = entry;
        foreach (var (key, entry) in file.Verdicts)
            transcript._verdicts[key] = entry;
        return transcript;
    }

    /// <summary>The committed fixture transcript shipped inside the assembly (the CLI smoke path).</summary>
    public static Transcript LoadEmbeddedFixture() =>
        FromJson(EmbeddedFixture.Read("fixture-transcript.json"));

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed record TranscriptFile(
        string Format,
        SortedDictionary<string, SolveEntry> Solves,
        SortedDictionary<string, VerdictEntry> Verdicts);
}

/// <summary>Reads the fixture assets embedded under <c>Fixtures/</c>.</summary>
internal static class EmbeddedFixture
{
    public static string Read(string fileName)
    {
        var name = $"Eidet.Bench.Fixtures.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Embedded fixture '{name}' not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
