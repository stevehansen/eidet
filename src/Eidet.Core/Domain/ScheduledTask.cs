namespace Eidet.Core.Domain;

/// <summary>
/// A persisted scheduled task in RavenDB. Uses the Refresh feature to trigger
/// execution at the scheduled time via the Changes API subscription.
///
/// Natural key: "scheduledtasks/{taskType}" (e.g., "scheduledtasks/maintenance")
/// </summary>
public class ScheduledTask
{
    public string Id { get; set; } = "";

    public ScheduledTaskType TaskType { get; set; }

    /// <summary>Interval between runs in hours.</summary>
    public int IntervalHours { get; set; }

    /// <summary>When this task last started executing.</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>When the last run completed.</summary>
    public DateTime? LastCompletedAt { get; set; }

    /// <summary>Duration of the last run in milliseconds.</summary>
    public long? LastDurationMs { get; set; }

    /// <summary>When the next run is scheduled.</summary>
    public DateTime NextRunAt { get; set; }

    /// <summary>Current status of this task.</summary>
    public ScheduledTaskStatus Status { get; set; } = ScheduledTaskStatus.Pending;

    /// <summary>Error message from the last failed run, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>Total number of times this task has run successfully.</summary>
    public int RunCount { get; set; }

    /// <summary>Total number of failed runs.</summary>
    public int ErrorCount { get; set; }

    /// <summary>When this task document was first created.</summary>
    public DateTime CreatedAt { get; set; }

    public static string MakeId(ScheduledTaskType type) => $"scheduledtasks/{type.ToString().ToLowerInvariant()}";
}

public enum ScheduledTaskType
{
    Maintenance,
    Consolidation
}

public enum ScheduledTaskStatus
{
    Pending,
    Running,
    Completed,
    Failed
}
