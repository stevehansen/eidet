using Eidet.Core.Domain;

namespace Eidet.Core.Tests.Domain;

public class ScheduledTaskTests
{
    [Fact]
    public void MakeId_Maintenance_ReturnsCorrectFormat()
    {
        var id = ScheduledTask.MakeId(ScheduledTaskType.Maintenance);
        Assert.Equal("scheduledtasks/maintenance", id);
    }

    [Fact]
    public void MakeId_Consolidation_ReturnsCorrectFormat()
    {
        var id = ScheduledTask.MakeId(ScheduledTaskType.Consolidation);
        Assert.Equal("scheduledtasks/consolidation", id);
    }

    [Theory]
    [InlineData(ScheduledTaskType.Maintenance)]
    [InlineData(ScheduledTaskType.Consolidation)]
    public void MakeId_AllTypes_StartWithPrefix(ScheduledTaskType type)
    {
        var id = ScheduledTask.MakeId(type);
        Assert.StartsWith("scheduledtasks/", id);
    }

    [Fact]
    public void NewTask_HasDefaultValues()
    {
        var task = new ScheduledTask();
        Assert.Equal("", task.Id);
        Assert.Equal(ScheduledTaskStatus.Pending, task.Status);
        Assert.Null(task.LastRunAt);
        Assert.Null(task.LastCompletedAt);
        Assert.Null(task.LastDurationMs);
        Assert.Null(task.LastError);
        Assert.Equal(0, task.RunCount);
        Assert.Equal(0, task.ErrorCount);
    }

    [Fact]
    public void ScheduledTaskStatus_HasExpectedValues()
    {
        Assert.Equal(0, (int)ScheduledTaskStatus.Pending);
        Assert.Equal(1, (int)ScheduledTaskStatus.Running);
        Assert.Equal(2, (int)ScheduledTaskStatus.Completed);
        Assert.Equal(3, (int)ScheduledTaskStatus.Failed);
    }

    [Fact]
    public void ScheduledTaskType_HasExpectedValues()
    {
        Assert.Equal(0, (int)ScheduledTaskType.Maintenance);
        Assert.Equal(1, (int)ScheduledTaskType.Consolidation);
    }

    [Fact]
    public void MakeId_IsIdempotent()
    {
        var id1 = ScheduledTask.MakeId(ScheduledTaskType.Maintenance);
        var id2 = ScheduledTask.MakeId(ScheduledTaskType.Maintenance);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void MakeId_DifferentTypes_ProduceDifferentIds()
    {
        var maintenance = ScheduledTask.MakeId(ScheduledTaskType.Maintenance);
        var consolidation = ScheduledTask.MakeId(ScheduledTaskType.Consolidation);
        Assert.NotEqual(maintenance, consolidation);
    }
}
