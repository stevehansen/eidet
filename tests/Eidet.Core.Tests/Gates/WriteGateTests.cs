using Eidet.Core.Domain;
using Eidet.Core.Gates;

namespace Eidet.Core.Tests.Gates;

public class WriteGateTests
{
    [Fact]
    public void Validate_PassesValidContent()
    {
        var result = WriteGate.Validate("The RavenDB index uses Corax engine for full-text search");
        Assert.True(result.Passed);
    }

    [Fact]
    public void Validate_SecretScannerRunsFirst()
    {
        var result = WriteGate.Validate("Config uses AKIAIOSFODNN7EXAMPLE as the AWS key");
        Assert.False(result.Passed);
        Assert.Contains("AWS", result.Reason);
    }

    [Fact]
    public void Validate_SignalGateRunsSecond()
    {
        var result = WriteGate.Validate("short");
        Assert.False(result.Passed);
        Assert.Contains("too short", result.Reason);
    }

    [Fact]
    public void Validate_PassesMemoryType()
    {
        // Self-talk should be blocked for Observations
        var obsResult = WriteGate.Validate("I will check the database connection next", MemoryType.Observation);
        Assert.False(obsResult.Passed);

        // Same content should pass for Heuristics
        var heurResult = WriteGate.Validate("I will always run migrations before tests in this repo", MemoryType.Heuristic);
        Assert.True(heurResult.Passed);
    }
}
