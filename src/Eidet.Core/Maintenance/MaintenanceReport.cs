namespace Eidet.Core.Maintenance;

public sealed class MaintenanceRequest
{
    public required string RepoId { get; init; }
    public bool IsRepoActive { get; init; } = true;
    public int ObservationRetentionDays { get; init; } = 90;
    public ISet<string>? OnlyStages { get; init; }
    public ISet<string>? SkipStages { get; init; }
}

public sealed class MaintenanceReport
{
    public required string RepoId { get; init; }
    public List<StageOutcome> Stages { get; } = [];
    public DateTime CompletedAt { get; set; }

    public int AffectedBy(string stageName) =>
        Stages.FirstOrDefault(s => s.Name == stageName).Affected;

    public IEnumerable<StageOutcome> Failures => Stages.Where(s => !s.Succeeded);

    public override string ToString()
    {
        var parts = Stages.Select(s => s.Succeeded
            ? $"{s.Name}={s.Affected}"
            : $"{s.Name}=ERROR({s.Error})");
        return $"Maintenance complete: {string.Join(", ", parts)}";
    }
}
