using UvexAdv.Phd2;
using Xunit;
namespace UvexAdv.Nina.Plugin.Tests;

public sealed class CatalogGuidingWindowTests
{
    [Theory]
    [InlineData(true,0,"PHD2_FRESH_GUIDE_WINDOW_DEADLINE")]
    [InlineData(true,3,"PHD2_GUIDE_WINDOW_NOT_STABLE")]
    [InlineData(false,4,"PHD2_GUIDE_WINDOW_NOT_STABLE")]
    public void ReportsCompletedRejectionsBeforeDeadline(bool expired,int complete,string code) =>
        Assert.Equal(code,Phd2PlacementGuideWindowPolicy.FailureCode(expired,complete));

    [Fact]
    public void OnlyMeasuredPositionsCanUseTheOptedInWarning()
    {
        Assert.False(Phd2PlacementGuideWindowPolicy.HasMeasuredPositionAuthority(Phd2TargetPositionAuthority.CatalogWcsProjection));
        Assert.False(Phd2PlacementGuideWindowPolicy.HasMeasuredPositionAuthority((Phd2TargetPositionAuthority)99));
        Assert.True(Phd2PlacementGuideWindowPolicy.HasMeasuredPositionAuthority(Phd2TargetPositionAuthority.CatalogWcsRegisteredField));
        // The real first recorded window stays a quality warning, not "within 2 pixels".
        Assert.False(Phd2PlacementGuideWindowPolicy.AllWithinTolerance([1.72665,3.28141,1.15137],2));
    }
}
