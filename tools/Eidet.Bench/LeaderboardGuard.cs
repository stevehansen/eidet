namespace Eidet.Bench;

/// <summary>
/// The anti-misreporting gate: a leaderboard-shaped number may only be published from the real
/// SWE Context Bench dataset. The bundled fixture exists to prove harness logic, never to
/// produce a citable figure. <c>eidet bench full</c> consults this before running; the fixture
/// smoke path prints <see cref="Refusal"/> so its output can't be screenshotted as a score.
/// </summary>
public static class LeaderboardGuard
{
    public const string DatasetUrl = "https://huggingface.co/datasets/jiayuanz3/SWEContextBench";

    public static bool MayPublish(ISweDatasetPort dataset) => dataset.IsRealDataset && dataset.IsAvailable;

    /// <summary>The message explaining why a leaderboard number is refused for this dataset.</summary>
    public static string Refusal(ISweDatasetPort dataset, string? datasetPath = null)
    {
        if (MayPublish(dataset))
            throw new ArgumentException($"Dataset '{dataset.Name}' is publishable — nothing to refuse.", nameof(dataset));

        if (!dataset.IsRealDataset)
            return $"Refusing to emit a leaderboard number: '{dataset.Name}' is a bundled fixture that only " +
                   $"proves harness logic. Download the real dataset ({DatasetUrl}) and run " +
                   "'eidet bench full --dataset <path>'.";

        var location = datasetPath is null ? "" : $" at '{datasetPath}'";
        return $"Refusing to emit a leaderboard number: the SWE Context Bench dataset was not found{location}. " +
               $"Download it from {DatasetUrl}, or run 'eidet bench' against the bundled fixture for an " +
               "offline smoke check.";
    }
}
