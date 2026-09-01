using Eidet.Core.Configuration;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Maintenance;

/// <summary>
/// The wall-clock half of the maintenance schedule. Written against whatever timezone the test
/// machine is in, so the assertions are properties ("lands on the anchor, in the future, and stays
/// there however long the run took") rather than fixed instants.
/// </summary>
public class MaintenanceScheduleTests
{
    private static readonly TimeOnly ThreeAm = new(3, 0);

    // Mid-month, mid-season dates: far from any DST transition, where the anchor unambiguously exists.
    public static TheoryData<DateTime> Instants => new()
    {
        new DateTime(2026, 1, 15, 1, 30, 0, DateTimeKind.Utc),
        new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 1, 15, 23, 45, 0, DateTimeKind.Utc),
        new DateTime(2026, 7, 15, 1, 30, 0, DateTimeKind.Utc),
        new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 7, 15, 23, 45, 0, DateTimeKind.Utc),
    };

    private static TimeOnly LocalTimeOf(DateTime utc) =>
        TimeOnly.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime());

    [Theory]
    [MemberData(nameof(Instants))]
    public void Daily_interval_always_lands_on_the_anchor(DateTime nowUtc)
    {
        Assert.Equal(ThreeAm, LocalTimeOf(ScheduledTaskService.NextOnGridUtc(ThreeAm, 24, nowUtc)));
    }

    [Theory]
    [MemberData(nameof(Instants))]
    public void Always_schedules_forward_never_into_the_past(DateTime nowUtc)
    {
        // A next-run in the past makes the poller fire the task on every tick.
        var next = ScheduledTaskService.NextOnGridUtc(ThreeAm, 24, nowUtc);

        Assert.True(next > nowUtc, $"{next:o} should be after {nowUtc:o}");
        Assert.True(next - nowUtc <= TimeSpan.FromHours(25), $"{next:o} is more than a day after {nowUtc:o}");
    }

    [Fact]
    public void A_long_run_does_not_push_the_next_one_later()
    {
        // The regression this grid exists for: scheduling from completion moved the nightly pass
        // forward by its own duration, every night, until it ran in the middle of the working day.
        var startedUtc = ScheduledTaskService.NextOnGridUtc(ThreeAm, 24, DateTime.UtcNow);
        var finishedUtc = startedUtc.AddHours(2).AddMinutes(19);

        Assert.Equal(ThreeAm, LocalTimeOf(ScheduledTaskService.NextOnGridUtc(ThreeAm, 24, finishedUtc)));
    }

    [Fact]
    public void The_anchor_holds_over_a_month_of_long_runs()
    {
        var slot = ScheduledTaskService.NextOnGridUtc(ThreeAm, 24, DateTime.UtcNow);

        for (var day = 0; day < 30; day++)
        {
            var finished = slot.AddHours(2).AddMinutes(19); // the observed duration that caused the walk
            var next = ScheduledTaskService.NextOnGridUtc(ThreeAm, 24, finished);

            Assert.Equal(ThreeAm, LocalTimeOf(next));
            Assert.Equal(1, (next.Date - slot.Date).Days);
            slot = next;
        }
    }

    [Fact]
    public void A_missed_window_collapses_to_the_next_slot_rather_than_a_backlog()
    {
        // Service down for three days: the next run is the upcoming anchor, not three of them.
        var slot = ScheduledTaskService.NextOnGridUtc(ThreeAm, 24, DateTime.UtcNow);
        var next = ScheduledTaskService.NextOnGridUtc(ThreeAm, 24, slot.AddDays(3).AddHours(1));

        Assert.Equal(ThreeAm, LocalTimeOf(next));
        Assert.True(next - slot <= TimeSpan.FromDays(5));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    public void A_sub_daily_interval_divides_the_day_from_the_anchor(int intervalHours)
    {
        var slot = ScheduledTaskService.NextOnGridUtc(ThreeAm, intervalHours, DateTime.UtcNow);
        var next = ScheduledTaskService.NextOnGridUtc(ThreeAm, intervalHours, slot.AddMinutes(1));

        Assert.Equal(TimeSpan.FromHours(intervalHours), next - slot);
    }

    [Fact]
    public void A_nonsense_interval_still_yields_a_time_in_the_future()
    {
        foreach (var interval in new[] { int.MinValue, -1, 0, int.MaxValue })
        {
            var now = DateTime.UtcNow;
            Assert.True(ScheduledTaskService.NextOnGridUtc(ThreeAm, interval, now) > now, $"interval {interval}");
        }
    }

    [Theory]
    [InlineData("03:00", 3, 0)]
    [InlineData("00:30", 0, 30)]
    [InlineData("23:59", 23, 59)]
    public void Reads_the_configured_anchor(string configured, int hour, int minute)
    {
        Assert.Equal(new TimeOnly(hour, minute), new MaintenanceConfig { AtLocalTime = configured }.ScheduledTime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("25:00")]
    public void Falls_back_to_three_am_rather_than_throwing(string configured)
    {
        // A typo in config.json should cost the configured hour, not the whole scheduler.
        Assert.Equal(ThreeAm, new MaintenanceConfig { AtLocalTime = configured }.ScheduledTime);
    }

    [Fact]
    public void Defaults_to_an_overnight_daily_pass()
    {
        var config = new MaintenanceConfig();

        Assert.Equal(24, config.IntervalHours);
        Assert.Equal(6, config.ConsolidationIntervalHours);
        Assert.Equal(ThreeAm, config.ScheduledTime);
    }
}
