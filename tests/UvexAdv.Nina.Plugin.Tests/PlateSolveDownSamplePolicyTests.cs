using UvexAdv.Nina.Plugin;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class PlateSolveDownSamplePolicyTests
{
    [Theory]
    [InlineData(1, true, null)]
    [InlineData(2, false, null)]
    [InlineData(2, true, 1)]
    [InlineData(3, true, null)]
    public void OnlySecondExistingTierGetsOneNativeResolutionRecovery(int tier, bool priorRejected, int? expected)
    {
        Assert.Equal(expected, PlateSolveDownSamplePolicy.G3RecoveryTierOverride(tier, priorRejected));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void G3SolvesUseAtLeastSoftwareTwoByTwo(int configured, int expected)
    {
        Assert.Equal(expected, PlateSolveDownSamplePolicy.EffectiveForRole(
            configured,
            "PHD2/G3 solve-only exposure ladder field tier 1"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void QhySolvesKeepNinaProfileValue(int configured)
    {
        Assert.Equal(configured, PlateSolveDownSamplePolicy.EffectiveForRole(
            configured,
            "QHY/GS350 coarse field"));
    }
}
