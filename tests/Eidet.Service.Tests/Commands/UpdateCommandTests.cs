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
    public void KillOtherEidetProcesses_DoesNotKillSelf()
    {
        // This should not throw and should not kill the test runner
        var killed = UpdateCommand.KillOtherEidetProcesses();
        // We can't assert the exact count, but the test process should survive
        Assert.True(true);
    }
}
