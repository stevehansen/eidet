using Eidet.Core.Configuration;
using Eidet.Core.Services;

namespace Eidet.Core.Tests.Update;

/// <summary>
/// The wall-clock half of the update schedule. Written against whatever timezone the test machine
/// is in, so the assertions are properties ("lands on 04:00 local, in the future, within a day")
/// rather than fixed instants.
/// </summary>
public class NightlyScheduleTests
{
    private static readonly TimeOnly FourAm = new(4, 0);

    // Mid-month, mid-season dates: far from any DST transition, where "04:00 local" is a time
    // that unambiguously exists.
    public static TheoryData<DateTime> Instants => new()
    {
        new DateTime(2026, 1, 15, 2, 30, 0, DateTimeKind.Utc),
        new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 1, 15, 23, 45, 0, DateTimeKind.Utc),
        new DateTime(2026, 7, 15, 2, 30, 0, DateTimeKind.Utc),
        new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 7, 15, 23, 45, 0, DateTimeKind.Utc),
    };

    [Theory]
    [MemberData(nameof(Instants))]
    public void Always_lands_on_the_requested_local_hour(DateTime nowUtc)
    {
        var next = ScheduledTaskService.NextLocalTimeUtc(FourAm, nowUtc);
        var local = DateTime.SpecifyKind(next, DateTimeKind.Utc).ToLocalTime();

        Assert.Equal(FourAm, TimeOnly.FromDateTime(local));
    }

    [Theory]
    [MemberData(nameof(Instants))]
    public void Always_schedules_forward_never_into_the_past(DateTime nowUtc)
    {
        // A next-run in the past makes the poller fire the task on every tick.
        var next = ScheduledTaskService.NextLocalTimeUtc(FourAm, nowUtc);

        Assert.True(next > nowUtc, $"{next:o} should be after {nowUtc:o}");
        Assert.True(next - nowUtc <= TimeSpan.FromHours(25), $"{next:o} is more than a day after {nowUtc:o}");
    }

    [Fact]
    public void Rolls_to_tomorrow_when_the_hour_has_already_passed_today()
    {
        var nowLocal = DateTime.Now.Date.AddHours(4).AddMinutes(1); // just past 04:00 local today
        var nowUtc = DateTime.SpecifyKind(nowLocal, DateTimeKind.Local).ToUniversalTime();

        var next = ScheduledTaskService.NextLocalTimeUtc(FourAm, nowUtc);
        var nextLocal = DateTime.SpecifyKind(next, DateTimeKind.Utc).ToLocalTime();

        Assert.Equal(nowLocal.Date.AddDays(1), nextLocal.Date);
    }

    [Theory]
    [InlineData("04:00", 4, 0)]
    [InlineData("00:30", 0, 30)]
    [InlineData("23:59", 23, 59)]
    public void Reads_the_configured_time(string configured, int hour, int minute)
    {
        var config = new UpdateConfig { AtLocalTime = configured };
        Assert.Equal(new TimeOnly(hour, minute), config.ScheduledTime);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("25:00")]
    public void Falls_back_to_four_am_rather_than_throwing(string configured)
    {
        // A typo in config.json should cost the configured hour, not the whole scheduler.
        Assert.Equal(FourAm, new UpdateConfig { AtLocalTime = configured }.ScheduledTime);
    }

    [Fact]
    public void Defaults_to_overnight_with_automation_off_and_a_day_long_age_gate()
    {
        var config = new UpdateConfig();

        Assert.True(config.Check);
        Assert.False(config.AutoUpdate);
        Assert.Equal(FourAm, config.ScheduledTime);
        Assert.Equal(24, config.MinimumAgeHours);
    }
}
