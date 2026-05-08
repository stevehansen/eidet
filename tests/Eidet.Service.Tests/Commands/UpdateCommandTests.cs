using Eidet.Service.Commands;

namespace Eidet.Service.Tests.Commands;

public class UpdateCommandTests
{
    [Fact]
    public async Task GetLatestNuGetVersion_ReturnsNullOrValid()
    {
        // This test is non-deterministic (depends on NuGet API availability)
        // but it should never throw
        var result = await UpdateCommand.GetLatestNuGetVersionAsync(CancellationToken.None);

        if (result != null)
        {
            // Should be a valid semver-like string
            Assert.Matches(@"^\d+\.\d+\.\d+", result);
        }
        // null is also acceptable (network issues, package not yet published)
    }

    [Fact]
    public void GenerateWindowsTrampolineScript_ContainsUpdateCommand()
    {
        var scriptPath = UpdateCommand.GenerateWindowsTrampolineScript("0.1.0", "0.3.0", restartService: true);

        try
        {
            Assert.True(File.Exists(scriptPath));
            var content = File.ReadAllText(scriptPath);

            Assert.Contains("dotnet tool update -g eidet", content);
            Assert.Contains("v0.1.0", content);
            Assert.Contains("v0.3.0", content);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public void GenerateWindowsTrampolineScript_PinsTargetVersion()
    {
        // Regression: without --version the script hits the NuGet search index, which
        // can lag publish by 10–30 min and silently re-resolve to the old version.
        var scriptPath = UpdateCommand.GenerateWindowsTrampolineScript("0.4.1", "0.4.2", restartService: false);

        try
        {
            var content = File.ReadAllText(scriptPath);
            Assert.Contains("dotnet tool update -g eidet --version 0.4.2", content);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public void GenerateWindowsTrampolineScript_RecordsHistoryAfterInstall()
    {
        // Regression: history must be recorded by the freshly-installed binary, not by
        // the running process before the install runs (which previously left bogus
        // entries when dotnet tool update silently no-op'd).
        var scriptPath = UpdateCommand.GenerateWindowsTrampolineScript("0.4.1", "0.4.2", restartService: false);

        try
        {
            var content = File.ReadAllText(scriptPath);
            Assert.Contains("eidet update --record-installed-from 0.4.1 --expected-version 0.4.2", content);
            // The verify step must come after the install and gate the success log line.
            var installIdx = content.IndexOf("dotnet tool update -g eidet --version 0.4.2", StringComparison.Ordinal);
            var recordIdx = content.IndexOf("eidet update --record-installed-from", StringComparison.Ordinal);
            Assert.True(installIdx >= 0 && recordIdx > installIdx,
                "record-installed-from must be invoked after dotnet tool update");
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public void GenerateWindowsTrampolineScript_IncludesServiceRestart_WhenRunning()
    {
        var scriptPath = UpdateCommand.GenerateWindowsTrampolineScript("0.1.0", "0.3.0", restartService: true);

        try
        {
            var content = File.ReadAllText(scriptPath);
            Assert.Contains("schtasks.exe /run /tn \"Eidet\"", content);
            Assert.Contains("Restarting Eidet service", content);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public void GenerateWindowsTrampolineScript_SkipsServiceRestart_WhenNotRunning()
    {
        var scriptPath = UpdateCommand.GenerateWindowsTrampolineScript("0.1.0", "0.3.0", restartService: false);

        try
        {
            var content = File.ReadAllText(scriptPath);
            Assert.DoesNotContain("schtasks.exe /run /tn \"Eidet\"", content);
            Assert.Contains("skipping restart", content);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public void GenerateWindowsTrampolineScript_WaitsForCallingPid()
    {
        var scriptPath = UpdateCommand.GenerateWindowsTrampolineScript("0.1.0", "0.3.0", restartService: false);

        try
        {
            var content = File.ReadAllText(scriptPath);
            // Should wait for the current process PID
            var pid = Environment.ProcessId.ToString();
            Assert.Contains($"PID eq {pid}", content);
            Assert.Contains("WAIT_LOOP", content);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public void GenerateWindowsTrampolineScript_SelfDeletes()
    {
        var scriptPath = UpdateCommand.GenerateWindowsTrampolineScript("0.1.0", "0.3.0", restartService: false);

        try
        {
            var content = File.ReadAllText(scriptPath);
            // Should contain self-delete command
            Assert.Contains("del \"%~f0\"", content);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public void SelectProcessesToKill_ExcludesCurrentPid()
    {
        var pids = new[] { 100, 200, 300, 999 };
        var result = UpdateCommand.SelectProcessesToKill(pids, currentPid: 200).ToArray();
        Assert.Equal(new[] { 100, 300, 999 }, result);
    }

    [Fact]
    public void SelectProcessesToKill_OnlyCurrent_ReturnsEmpty()
    {
        var result = UpdateCommand.SelectProcessesToKill(new[] { 42 }, currentPid: 42);
        Assert.Empty(result);
    }

    [Fact]
    public void SelectProcessesToKill_NoCandidates_ReturnsEmpty()
    {
        var result = UpdateCommand.SelectProcessesToKill([], currentPid: 42);
        Assert.Empty(result);
    }

    [Fact]
    public void SelectProcessesToKill_CurrentNotPresent_ReturnsAll()
    {
        var pids = new[] { 1, 2, 3 };
        var result = UpdateCommand.SelectProcessesToKill(pids, currentPid: 999).ToArray();
        Assert.Equal(pids, result);
    }
}
