using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class QhyCoarseCenteringLimitsTests
{
    [Fact]
    public void ValidEnvelopeIsIndependentAndVersioned()
    {
        var limits = new QhyCoarseCenteringLimits(1, 600, 2400, 8, TimeSpan.FromMinutes(10));

        Assert.Empty(limits.Validate());
        Assert.Equal(1, QhyCoarseCenteringLimits.CurrentSchemaVersion);
    }

    [Fact]
    public void EnvelopeMustReserveReturnAndRejectWrongSchema()
    {
        var limits = new QhyCoarseCenteringLimits(2, 600, 900, 1, TimeSpan.Zero);

        var issues = limits.Validate();

        Assert.Contains(issues, issue => issue.Contains("schema", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Contains("safe return", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Contains("attempt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Contains("elapsed", StringComparison.OrdinalIgnoreCase));
    }
}
