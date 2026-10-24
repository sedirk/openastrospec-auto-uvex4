using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3FieldOverlapPolicyTests
{
    private static readonly G3OverlapFootprint HistoricalGeometry = new(
        1920, 1080, 0.383, ImageXAxisEastToNorthDegrees: null,
        EndpointPositionUncertaintyArcseconds: 5, FractionalScaleUncertainty: 0.05);

    private static readonly G3LocalSearchLimits SearchLimits = new(
        G3LocalSearchPattern.SquareSpiral, 300, 900, 3600, 12, TimeSpan.FromMinutes(5));

    [Fact]
    public void HistoricalShortSideCannotUseOldThreeHundredArcsecondGrid()
    {
        var plan = G3FieldOverlapPolicy.PlanSearch(SearchLimits, HistoricalGeometry);

        Assert.True(plan.Overlap.Usable);
        Assert.True(plan.Overlap.UsedUnknownOrientationBound);
        Assert.Equal(392.958, plan.Overlap.MinimumHeightArcseconds, 9);
        Assert.InRange(plan.EffectiveLimits.StepArcseconds, 186, 187);
        Assert.True(plan.EffectiveLimits.StepArcseconds < SearchLimits.StepArcseconds);
        Assert.Equal(SearchLimits with { StepArcseconds = plan.EffectiveLimits.StepArcseconds }, plan.EffectiveLimits);
        Assert.Equal(SearchLimits.MaximumAttempts, plan.Waypoints.Count);
        Assert.All(plan.Waypoints, point => Assert.Equal(plan.EffectiveLimits.StepArcseconds, point.MoveFromPreviousArcseconds, 8));
    }

    [Fact]
    public void KnownPositionAngleUsesTheCorrectAxisAndRotatesWithTheCamera()
    {
        var geometry = HistoricalGeometry with { ImageXAxisEastToNorthDegrees = 0, FractionalScaleUncertainty = 0 };
        var east = G3FieldOverlapPolicy.Bound(geometry, 1, 0);
        var north = G3FieldOverlapPolicy.Bound(geometry, 0, 1);
        var rotated = G3FieldOverlapPolicy.Bound(geometry with { ImageXAxisEastToNorthDegrees = 90 }, 1, 0);

        Assert.Equal(1920 * 0.383 / 2 - 10, east.MaximumStepArcseconds, 8);
        Assert.Equal(1080 * 0.383 / 2 - 10, north.MaximumStepArcseconds, 8);
        Assert.Equal(north.MaximumStepArcseconds, rotated.MaximumStepArcseconds, 8);
    }

    [Fact]
    public void DiagonalCommandRetainsBothAxisOverlapAndFullTwoEndpointError()
    {
        var geometry = HistoricalGeometry with { ImageXAxisEastToNorthDegrees = 17, OrientationUncertaintyDegrees = 0.2 };
        var move = G3FieldOverlapPolicy.CapMove(geometry, 520, 537, 1200);
        var angle = 17 * Math.PI / 180;
        var dx = Math.Abs(move.RaTangentArcseconds * Math.Cos(angle) + move.DeclinationArcseconds * Math.Sin(angle));
        var dy = Math.Abs(-move.RaTangentArcseconds * Math.Sin(angle) + move.DeclinationArcseconds * Math.Cos(angle));

        Assert.True(move.Overlap.Usable);
        Assert.Equal(520d / 537, move.RaTangentArcseconds / move.DeclinationArcseconds, 10);
        Assert.True(move.Overlap.MinimumWidthArcseconds - dx - 2 * move.Overlap.EndpointMarginArcseconds >=
            move.Overlap.MinimumWidthArcseconds * 0.5 - 1e-8);
        Assert.True(move.Overlap.MinimumHeightArcseconds - dy - 2 * move.Overlap.EndpointMarginArcseconds >=
            move.Overlap.MinimumHeightArcseconds * 0.5 - 1e-8);
    }

    [Fact]
    public void OriginalNarrowerCeilingAndAlreadySmallMoveAreNeverEnlarged()
    {
        var small = G3FieldOverlapPolicy.CapMove(HistoricalGeometry, 6, 8, 30);
        var narrow = G3FieldOverlapPolicy.CapMove(HistoricalGeometry, 600, 800, 30);
        Assert.Equal(1, small.AppliedScale);
        Assert.Equal(6, small.RaTangentArcseconds);
        Assert.Equal(8, small.DeclinationArcseconds);
        Assert.Equal(0.03, narrow.AppliedScale, 10);
        var smallSearch = SearchLimits with { StepArcseconds = 20 };
        Assert.Equal(smallSearch, G3FieldOverlapPolicy.PlanSearch(smallSearch, HistoricalGeometry).EffectiveLimits);
    }

    [Fact]
    public void EveryUnknownOrientationIsCoveredByTheShortSideBound()
    {
        var unknown = G3FieldOverlapPolicy.Bound(HistoricalGeometry, 1, 0);
        for (var angle = -180; angle <= 180; angle++)
        {
            var known = G3FieldOverlapPolicy.Bound(HistoricalGeometry with { ImageXAxisEastToNorthDegrees = angle }, 1, 0);
            Assert.True(known.MaximumStepArcseconds + 1e-9 >= unknown.MaximumStepArcseconds);
        }
    }

    [Fact]
    public void LargerUncertaintyCanOnlyTightenTheCap()
    {
        var baseline = HistoricalGeometry with { ImageXAxisEastToNorthDegrees = 0 };
        var zero = G3FieldOverlapPolicy.Bound(baseline, 1, 1);
        var endpoint = G3FieldOverlapPolicy.Bound(baseline with { EndpointPositionUncertaintyArcseconds = 15 }, 1, 1);
        var scale = G3FieldOverlapPolicy.Bound(baseline with { FractionalScaleUncertainty = 0.15 }, 1, 1);
        var rotation = G3FieldOverlapPolicy.Bound(baseline with { OrientationUncertaintyDegrees = 0.3 }, 1, 1);
        Assert.True(endpoint.MaximumStepArcseconds < zero.MaximumStepArcseconds);
        Assert.True(scale.MaximumStepArcseconds < zero.MaximumStepArcseconds);
        Assert.True(rotation.MaximumStepArcseconds < zero.MaximumStepArcseconds);
        var unknown = G3FieldOverlapPolicy.Bound(HistoricalGeometry, 1, 1);
        var unknownWithRotation = G3FieldOverlapPolicy.Bound(
            HistoricalGeometry with { OrientationUncertaintyDegrees = 0.3 }, 1, 1);
        Assert.True(unknownWithRotation.MaximumStepArcseconds < unknown.MaximumStepArcseconds);
    }

    [Fact]
    public void UnusableGeometryDisablesMosaicWithoutChangingExistingFallbackLimits()
    {
        foreach (var geometry in new[]
        {
            HistoricalGeometry with { WidthPixels = 0 },
            HistoricalGeometry with { PixelScaleArcseconds = double.NaN },
            HistoricalGeometry with { PixelScaleArcseconds = double.PositiveInfinity },
            HistoricalGeometry with { FractionalScaleUncertainty = 1 },
            HistoricalGeometry with { EndpointPositionUncertaintyArcseconds = 300 },
            HistoricalGeometry with { ImageXAxisEastToNorthDegrees = double.NaN },
            HistoricalGeometry with { ImageXAxisEastToNorthDegrees = 0, OrientationUncertaintyDegrees = 90 },
        })
        {
            var plan = G3FieldOverlapPolicy.PlanSearch(SearchLimits, geometry);
            Assert.False(plan.Overlap.Usable);
            Assert.Equal(SearchLimits, plan.EffectiveLimits);
            Assert.Equal(G3LocalSearchPlanner.Build(SearchLimits), plan.Waypoints);
        }
    }

    [Fact]
    public void HistoricalSevenHundredFortySevenArcsecondCorrectionCannotBreakTheChainInOneMove()
    {
        var plan = G3FieldOverlapPolicy.CapMove(HistoricalGeometry, 747, 0, 2700);
        Assert.True(plan.Overlap.Usable);
        Assert.InRange(plan.RaTangentArcseconds, 186, 187);
        Assert.Equal(0, plan.DeclinationArcseconds);
        // This needs at least five outbound observations, but DOES NOT grant
        // those observations: existing actions and guaranteed return still gate
        // every step. In particular a four-action envelope cannot finish it.
        Assert.Equal(5, Math.Ceiling(747 / plan.RaTangentArcseconds));
        Assert.Equal(12, SearchLimits.MaximumAttempts);
    }

    [Fact]
    public void TinyPositiveOverlapStepCannotExpandGridConstructionBeyondOriginalActionCount()
    {
        var tiny = HistoricalGeometry with
        {
            PixelScaleArcseconds = 1e-9,
            EndpointPositionUncertaintyArcseconds = 0,
        };
        var plan = G3FieldOverlapPolicy.PlanSearch(SearchLimits, tiny);
        Assert.True(plan.Overlap.Usable);
        Assert.Equal(SearchLimits.MaximumAttempts, plan.Waypoints.Count);
        Assert.Equal(SearchLimits.MaximumAttempts, plan.EffectiveLimits.MaximumAttempts);
        Assert.All(plan.Waypoints, point => Assert.True(point.RadiusArcseconds < 1e-5));
    }
}
