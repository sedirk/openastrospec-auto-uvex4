using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class CatalogShortPositionSourceSafetyTests
{
    [Fact]
    public void SharedProductionRouteChecksBothImmutableBindingsAndNeverSolvesOrMovesInShortCheck()
    {
        var source=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.CatalogShortPosition.cs"));
        Assert.Equal(3, source.Split("ValidateG3FieldMountBindingForMotionAsync").Length-1);
        Assert.Contains("ValidateG3ProbeMountBindingForMotionAsync",source);
        Assert.Contains("ValidateIdentityAsync",source);
        Assert.Contains("RequireImmediatePhysicalActionGatesAsync",source);
        Assert.Contains("G3SepShortPositionPolicy.Measure",source);
        Assert.Contains("G3SepCatalogPrimaryPolicy.Measure",source);
        Assert.Contains("CatalogPrimaryAstrometryReader.ReadAsync",source);
        Assert.Contains("originalPlanPrediction =",source);
        Assert.Contains("planCoordinatesChanged = false",source);
        Assert.DoesNotContain("G3ResolvedCompanionPositionPolicy.Resolve",source);
        Assert.DoesNotContain("G3ShortPositionMeasurementPolicy.Measure(",source);
        Assert.Contains("sepClient.DetectAsync",source);
        Assert.Contains("sepWorkerSha256",source);
        Assert.Contains("sepModuleSha256",source);
        Assert.Contains("sepMeasurements,",source);
        Assert.Contains("G3ShortPositionMeasurementPolicy.EvaluateConfirmation",source);
        Assert.Contains("if (!confirmation.RetryAllowed)",source);
        Assert.Contains("if (!confirmation.RetainPreviousMeasurement)",source);
        Assert.Contains("confirmation.Gate.Metrics",source);
        Assert.Contains("previousCompletedUtc = capture.CompletedUtc",source);
        Assert.Contains("attempt <= G3ShortPositionMeasurementPolicy.MaximumFrames",source);
        Assert.Contains("!frameHashes.Add(sha)",source);
        Assert.Contains("capture.CompletedUtc <= previousCompletedUtc",source);
        Assert.Contains("ComputeFileSha256Async(previousFramePath",source);
        Assert.Contains("CatalogPositionSpreadPixels = positionSpread",source);
        Assert.Contains("CatalogPositionRefinedFromSameFrame = false",source);
        Assert.Contains("BoundShortPositionEvidencePath = receipt",source);
        Assert.Contains("BeforeExposureMountReadback: before",source);
        Assert.Contains("UsesCatalogWcsTargetAuthority(context)",source);
        Assert.DoesNotContain("SolveImageAsync",source);
        Assert.DoesNotContain("Slew",source);
        Assert.DoesNotContain("SetLock",source);
        Assert.DoesNotContain("MainFocusMeasurement =",source);
        var runner=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.cs"));
        Assert.Contains("RefineClippedCatalogPositionAsync(context, solvedField",runner);
        Assert.Contains("currentField.TargetIdentification.HasCatalogPositionRefinement",runner);
        Assert.Contains("currentResidual + currentField.TargetIdentification.CatalogPositionSpreadPixels <= placementPreset.CoarseHandoffResidualPixels",runner);
        Assert.Contains("coarseResidualPixels + lastG3Field.TargetIdentification.CatalogPositionSpreadPixels",runner);
        var handoff=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.Handoff.cs"));
        Assert.Contains("field.TargetIdentification.CatalogPositionSpreadPixels, preset.CoarseHandoffResidualPixels",handoff);
        Assert.Contains("attempts.Count == 0 ? \"G3_SEARCH_NOT_STARTED_RETURNED\"",runner);
    }
}
