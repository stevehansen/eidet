using Eidet.Service.Commands;

namespace Eidet.Service.Tests.Commands;

/// <summary>
/// #97: an unattended install reports only to update.log, so <c>eidet status</c> reads the last
/// outcome back. Most line shapes below are what the retired Windows .cmd trampoline wrote — they
/// stay readable because existing logs still end in them.
/// </summary>
public class UpdateLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"eidet-update-log-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private string? LastFailure(string current, params string[] lines)
    {
        File.WriteAllLines(_path, lines);
        return UpdateLog.LastUnresolvedFailure(current, _path);
    }

    [Fact]
    public void A_verify_failure_for_a_version_still_not_installed_is_reported()
    {
        var failure = LastFailure("0.14.1",
            "04/09/2026 16:05:08,77 - Updated from v0.14.0 to v0.14.1 ",
            "VERIFY FAILED ",
            "23/09/2026 11:11:50,23 - Update from v0.14.1 to v0.14.2 could not be verified ",
            "Installed binary did not report v0.14.2 — dotnet tool update may have silently re-resolved. ",
            "23/09/2026 11:11:50,26 - Service restarted ");

        Assert.Equal("23/09/2026 11:11:50,23 - Update from v0.14.1 to v0.14.2 could not be verified", failure);
    }

    [Fact]
    public void An_install_failure_is_reported()
    {
        var failure = LastFailure("0.13.0",
            "UPDATE FAILED ",
            "04/09/2026 16:05:08,77 - Update from v0.13.0 to v0.14.0 FAILED after 5 attempts ",
            "dotnet tool update -g eidet --version 0.14.0 returned error ");

        Assert.Contains("to v0.14.0 FAILED", failure);
    }

    [Fact]
    public void A_later_success_clears_the_failure()
    {
        Assert.Null(LastFailure("0.14.2",
            "23/09/2026 11:11:50,23 - Update from v0.14.1 to v0.14.2 could not be verified ",
            "23/09/2026 11:13:34,59 - Updated from v0.14.1 to v0.14.2 ",
            "23/09/2026 11:13:34,61 - Service restarted "));
    }

    [Fact]
    public void A_failure_whose_version_was_installed_another_way_is_not_reported()
    {
        // e.g. a manual `dotnet tool update` that the script never saw.
        Assert.Null(LastFailure("0.14.2",
            "23/09/2026 11:11:50,23 - Update from v0.14.1 to v0.14.2 could not be verified "));
    }

    [Fact]
    public void A_missing_log_reports_nothing()
    {
        Assert.Null(UpdateLog.LastUnresolvedFailure("0.14.1", _path));
    }

    [Fact]
    public void Appended_outcomes_round_trip()
    {
        // A multi-line error (dotnet's own output) must not hide the outcome line above it.
        UpdateLog.Append("Update from v0.14.4 to v0.14.5 FAILED: dotnet tool update failed:\nAccess denied", _path);
        Assert.EndsWith("- Update from v0.14.4 to v0.14.5 FAILED: dotnet tool update failed:",
            UpdateLog.LastUnresolvedFailure("0.14.4", _path));

        UpdateLog.Append("Updated from v0.14.4 to v0.14.5", _path);
        Assert.Null(UpdateLog.LastUnresolvedFailure("0.14.4", _path));
    }
}
