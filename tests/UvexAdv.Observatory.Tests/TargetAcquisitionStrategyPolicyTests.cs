using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class TargetAcquisitionStrategyPolicyTests
{
    private static readonly EquatorialTarget Target = new("test target", "catalog-1", 30, 40);
    private static readonly PixelPoint Prediction = new(100, 100);
    private static MonochromeFrame Frame() => new(240, 240, new ushort[240 * 240], 65520);
    private static TargetCatalogMetadata Metadata(string type, string? subtype = null) => new(
        Target.Name, Target.CatalogId, Target.RightAscensionDegrees, Target.DeclinationDegrees,
        "Stellarium", type, 5, DateTimeOffset.Parse("2026-09-13T17:00:00Z"), subtype);
    private static StarCandidate Star(double x, double y) => new(new(x, y), 5000, 10000, 30, 3, 0, 0, 50);

    [Fact]
    public void SerializedManualEnumValuesRemainUnchanged()
    {
        Assert.Equal(0, (int)TargetObservabilityClass.DirectStellar);
        Assert.Equal(4, (int)TargetObservabilityClass.InvisibleInG3);
        Assert.Equal(5, (int)TargetObservabilityClass.AutoFromPlanetarium);
    }

    [Theory]
    [InlineData("Star", null, TargetObservabilityClass.DirectStellar)]
    [InlineData("Nebula", "planetary nebula", TargetObservabilityClass.CompactExtended)]
    [InlineData("Nebula", "quasar", TargetObservabilityClass.FaintPointSource)]
    [InlineData("Nebula", "galaxy", TargetObservabilityClass.ExtendedNebula)]
    public void AutoResolvesBoundMetadataBeforeAnyFrame(string type, string? subtype, TargetObservabilityClass expected)
    {
        var strategy = TargetAcquisitionStrategyPolicy.Resolve(TargetObservabilityClass.AutoFromPlanetarium, Target, Metadata(type, subtype));
        Assert.Equal(expected, strategy.EffectiveClass);
        Assert.Equal(GateDisposition.Passed, strategy.Gate.Disposition);
        Assert.Equal(expected != TargetObservabilityClass.DirectStellar, strategy.UsesCatalogPositionOnly);
        Assert.Equal(TargetObservabilityClass.AutoFromPlanetarium, strategy.RequestedClass);
    }

    [Fact]
    public void UnknownOrStaleMetadataNeverSelectsInvisibleTarget()
    {
        foreach (var metadata in new[] { null, Metadata("unknown"), Metadata("Nebula") with { TargetName = "another target" } })
        {
            var strategy = TargetAcquisitionStrategyPolicy.Resolve(TargetObservabilityClass.AutoFromPlanetarium, Target, metadata);
            Assert.Equal(TargetObservabilityClass.DirectStellar, strategy.EffectiveClass);
            Assert.False(strategy.UsesCatalogPositionOnly);
            Assert.Equal(GateSeverity.Warning, strategy.Gate.Severity);
        }
    }

    [Theory]
    [InlineData(TargetObservabilityClass.AutoFromPlanetarium)]
    [InlineData(TargetObservabilityClass.DirectStellar)]
    [InlineData(TargetObservabilityClass.ExtendedNebula)]
    public void KnownMovingOrSolarObjectCannotUseManualOverride(TargetObservabilityClass requested)
    {
        var strategy = TargetAcquisitionStrategyPolicy.Resolve(requested, Target, Metadata("Planet", "star"));
        Assert.Equal(GateDisposition.Failed, strategy.Gate.Disposition);
        Assert.Equal("TARGET_STRATEGY_UNSUPPORTED_OBJECT", strategy.Gate.Code);
    }

    [Fact]
    public void PlanRejectsKnownSolarObjectBeforeRunnerOrRecoveryCanStart()
    {
        var plan = new ObservationPlan("run", "night", Target, new(30, 120, 0),
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), new(), new(), "atr", "g3", "qhy",
            TargetObservability: TargetObservabilityClass.DirectStellar, CatalogMetadata: Metadata("Planet", "star"));
        Assert.Contains(plan.Validate(), issue => issue.StartsWith("TARGET_STRATEGY_UNSUPPORTED_OBJECT", StringComparison.Ordinal));
        Assert.Empty((plan with { CatalogMetadata = Metadata("Star") }).Validate());
    }

    [Fact]
    public void DirectStarUsesUniqueMeasuredPosition()
    {
        var strategy = TargetAcquisitionStrategyPolicy.Resolve(TargetObservabilityClass.DirectStellar, Target, null);
        var decision = TargetAcquisitionStrategyPolicy.IdentifyFromVerifiedWcs(strategy, Frame(), [Star(105, 100)], Prediction, 50);
        Assert.Equal(TargetAcquisitionBranch.DirectStellarPosition, decision.Branch);
        Assert.False(decision.IsFallback);
        Assert.True(decision.Identification.CatalogPositionRefinedFromSameFrame);
        Assert.Equal(new PixelPoint(105, 100), decision.Identification.Target!.Centroid);
    }

    [Fact]
    public void MissingPeakFallsBackToSameWcsWithoutChangingClassOrInventingFlux()
    {
        var strategy = TargetAcquisitionStrategyPolicy.Resolve(TargetObservabilityClass.DirectStellar, Target, null);
        var decision = TargetAcquisitionStrategyPolicy.IdentifyFromVerifiedWcs(strategy, Frame(), [], Prediction, 50);
        Assert.True(decision.IsFallback);
        Assert.Equal(TargetAcquisitionBranch.CatalogWcsGeometry, decision.Branch);
        Assert.Equal(TargetObservabilityClass.DirectStellar, strategy.EffectiveClass);
        Assert.Equal(Prediction, decision.Identification.Target!.Centroid);
        Assert.Equal(0, decision.Identification.Target.FluxAdu);
        Assert.False(decision.Identification.HasCatalogPositionRefinement);
    }

    [Fact]
    public void AmbiguousLocalPeaksAreNotChosenOrAppliedAsAnOffset()
    {
        var strategy = TargetAcquisitionStrategyPolicy.Resolve(TargetObservabilityClass.DirectStellar, Target, null);
        var decision = TargetAcquisitionStrategyPolicy.IdentifyFromVerifiedWcs(strategy, Frame(),
            [Star(105, 100), Star(94, 100)], Prediction, 50);
        Assert.True(decision.IsFallback);
        Assert.Equal(TargetAcquisitionBranch.CatalogWcsGeometry, decision.Branch);
        Assert.Equal(Prediction, decision.Identification.Target!.Centroid);
        Assert.False(decision.Identification.HasCatalogPositionRefinement);
        Assert.Contains("uniqueness", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClippedRecognitionRegionSelectsBoundedShortSepInsteadOfClaimingCentroid()
    {
        var pixels = new ushort[240 * 240];
        pixels[100 * 240 + 100] = 65520;
        var strategy = TargetAcquisitionStrategyPolicy.Resolve(TargetObservabilityClass.DirectStellar, Target, null);
        var decision = TargetAcquisitionStrategyPolicy.IdentifyFromVerifiedWcs(strategy, new(240, 240, pixels, 65520),
            [Star(110, 100)], Prediction, 50);
        Assert.Equal(TargetAcquisitionBranch.ShortExposureSep, decision.Branch);
        Assert.True(decision.IsFallback);
        Assert.False(decision.Identification.HasCatalogPositionRefinement);
    }

    [Theory]
    [InlineData(TargetObservabilityClass.FaintPointSource)]
    [InlineData(TargetObservabilityClass.CompactExtended)]
    [InlineData(TargetObservabilityClass.ExtendedNebula)]
    [InlineData(TargetObservabilityClass.InvisibleInG3)]
    public void ExtendedOrInvisibleNeverSnapsToFieldStar(TargetObservabilityClass requested)
    {
        var strategy = TargetAcquisitionStrategyPolicy.Resolve(requested, Target, null);
        var decision = TargetAcquisitionStrategyPolicy.IdentifyFromVerifiedWcs(strategy, Frame(), [Star(101, 100)], Prediction, 50);
        Assert.Equal(TargetAcquisitionBranch.CatalogWcsGeometry, decision.Branch);
        Assert.Equal(Prediction, decision.Identification.Target!.Centroid);
        Assert.False(decision.Identification.HasCatalogPositionRefinement);
        Assert.DoesNotContain(strategy.OrderedBranches, branch => branch.Id == TargetAcquisitionBranch.DirectStellarPosition);
    }

    [Theory]
    [InlineData("G3_PLATE_SOLVE_FAILED", true)]
    [InlineData("TARGET_NOT_FOUND", true)]
    [InlineData("TARGET_AMBIGUOUS", true)]
    [InlineData("PHD2_IDENTITY_MISMATCH", false)]
    [InlineData("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", false)]
    [InlineData("G3_MOTION_RETURN_BUDGET_EXHAUSTED", false)]
    [InlineData("SAFETY_UNSAFE", false)]
    [InlineData("anything_else", false)]
    public void NeighborFallbackIsAllowlistedAndNeverRestartsExhaustedBudgets(string code, bool expected)
    {
        Assert.Equal(expected, TargetAcquisitionStrategyPolicy.MayTryBoundedNeighbor(GateResult.Unknown(code, "test")));
    }

    [Fact]
    public void OutsideFrameDoesNotBecomeAnAcceptedTargetThroughFallback()
    {
        var strategy = TargetAcquisitionStrategyPolicy.Resolve(TargetObservabilityClass.DirectStellar, Target, null);
        var decision = TargetAcquisitionStrategyPolicy.IdentifyFromVerifiedWcs(strategy, Frame(), [], new(-10, 100), 50);
        Assert.NotEqual(GateDisposition.Passed, decision.Identification.Gate.Disposition);
        Assert.Null(decision.Identification.Target);
    }
}
