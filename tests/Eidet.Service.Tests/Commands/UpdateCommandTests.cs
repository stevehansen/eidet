using Eidet.Service.Commands;

namespace Eidet.Service.Tests.Commands;

public class UpdateCommandTests
{
    // The NuGet lookup moved to Eidet.Core's UpdateChecker, where it is covered offline against
    // fixed payloads (UpdateCheckerTests) rather than against the live API.

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
    public void GenerateWindowsTrampolineScript_RetriesUpdateToOutraceRespawn()
    {
        // Regression: an MCP client supervising `eidet mcp` respawns it after we kill it,
        // re-locking the tool store before dotnet tool update can replace it. The script
        // must re-kill and retry the update rather than abort on the first lock conflict.
        var scriptPath = UpdateCommand.GenerateWindowsTrampolineScript("0.6.0", "0.7.0", restartService: true);

        try
        {
            var content = File.ReadAllText(scriptPath);

            // The kill must live inside the retry loop, so each attempt re-kills the
            // respawned process immediately before re-running the update.
            var loopIdx = content.IndexOf(":UPDATE_LOOP", StringComparison.Ordinal);
            var killIdx = loopIdx >= 0 ? content.IndexOf("taskkill /f /im eidet.exe", loopIdx, StringComparison.Ordinal) : -1;
            var updateIdx = killIdx >= 0 ? content.IndexOf("dotnet tool update -g eidet --version 0.7.0", killIdx, StringComparison.Ordinal) : -1;
            var retryIdx = updateIdx >= 0 ? content.IndexOf("goto UPDATE_LOOP", updateIdx, StringComparison.Ordinal) : -1;

            Assert.True(loopIdx >= 0 && killIdx > loopIdx && updateIdx > killIdx && retryIdx > updateIdx,
                "retry loop must re-kill eidet, run the update, then loop back on failure");
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenerateWindowsTrampolineScript_LeavesNoUnexpandedPlaceholders(bool restartService)
    {
        // Regression: the restart branch was a nested raw string literal without the `$`
        // prefix, so `{logPath}` reached the .cmd verbatim and `>> "{logPath}"` created a
        // file literally named `{logPath}` in the working directory instead of appending
        // to update.log. Any brace surviving into the script means an interpolation was
        // lost — the script has no legitimate use for one.
        var scriptPath = UpdateCommand.GenerateWindowsTrampolineScript("0.1.0", "0.3.0", restartService);

        try
        {
            var content = File.ReadAllText(scriptPath);
            Assert.DoesNotContain("{", content);
            Assert.DoesNotContain("}", content);
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
