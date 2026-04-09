using Eidet.Core.Domain;

namespace Eidet.Core.Tests.Domain;

public class ValidityTests
{
    [Fact]
    public void IsCurrentlyValid_TrueWhenNoExpiry()
    {
        var validity = new Validity { ValidFrom = DateTime.UtcNow.AddDays(-1) };
        Assert.True(validity.IsCurrentlyValid);
    }

    [Fact]
    public void IsCurrentlyValid_FalseWhenExpired()
    {
        var validity = new Validity
        {
            ValidFrom = DateTime.UtcNow.AddDays(-10),
            ValidUntil = DateTime.UtcNow.AddDays(-1),
        };
        Assert.False(validity.IsCurrentlyValid);
    }

    [Fact]
    public void IsCurrentlyValid_FalseWhenNotYetValid()
    {
        var validity = new Validity { ValidFrom = DateTime.UtcNow.AddDays(1) };
        Assert.False(validity.IsCurrentlyValid);
    }

    [Fact]
    public void DefaultValidFrom_IsNotUtcNow()
    {
        // Verify the non-deterministic default was removed (issue #5)
        var validity = new Validity();
        Assert.Equal(default, validity.ValidFrom);
    }

    [Fact]
    public void IsValidAt_ChecksSpecificTime()
    {
        var validity = new Validity
        {
            ValidFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ValidUntil = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        Assert.True(validity.IsValidAt(new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.False(validity.IsValidAt(new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.False(validity.IsValidAt(new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc)));
    }
}
