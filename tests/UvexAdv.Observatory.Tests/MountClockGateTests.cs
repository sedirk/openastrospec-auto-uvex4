using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class MountClockGateTests
{
    private static readonly DateTimeOffset SystemUtc =
        new(2026, 8, 16, 16, 14, 57, TimeSpan.Zero);

    [Fact]
    public void PassesClockWithinInclusiveLimit()
    {
        var result = MountClockGate.Evaluate(
            SystemUtc.UtcDateTime.AddSeconds(-60),
            SystemUtc,
            TimeSpan.FromSeconds(60));

        Assert.Equal(GateDisposition.Passed, result.Disposition);
        Assert.Equal("MOUNT_CLOCK_VALID", result.Code);
        Assert.Equal(-60, result.Metrics!["mountClockOffsetSeconds"]);
    }

    [Fact]
    public void FailsKnownTwoDayOffset()
    {
        var result = MountClockGate.Evaluate(
            new DateTime(2026, 8, 14, 16, 14, 55, DateTimeKind.Unspecified),
            SystemUtc,
            TimeSpan.FromSeconds(60));

        Assert.Equal(GateDisposition.Failed, result.Disposition);
        Assert.Equal("MOUNT_CLOCK_OFFSET_EXCEEDED", result.Code);
        Assert.Equal(-172802, result.Metrics!["mountClockOffsetSeconds"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0001-01-01T00:00:00")]
    [InlineData("9999-12-31T23:59:59.9999999")]
    public void TreatsMissingOrSentinelClockAsIndeterminate(string? value)
    {
        DateTime? reported = value is null ? null : DateTime.Parse(value);

        var result = MountClockGate.Evaluate(
            reported,
            SystemUtc,
            TimeSpan.FromSeconds(60));

        Assert.Equal(GateDisposition.Indeterminate, result.Disposition);
        Assert.Equal("MOUNT_CLOCK_UNAVAILABLE", result.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(301)]
    public void RejectsUnsafePolicy(double seconds)
    {
        var result = MountClockGate.Evaluate(
            SystemUtc.UtcDateTime,
            SystemUtc,
            TimeSpan.FromSeconds(seconds));

        Assert.Equal(GateDisposition.Indeterminate, result.Disposition);
        Assert.Equal("MOUNT_CLOCK_POLICY_INVALID", result.Code);
    }
}
