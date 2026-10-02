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
        Assert.Contains("attempt <= exposurePolicy.MaximumFrames",source);
        Assert.Contains("exposurePolicy.AfterMeasurement(measurement.Gate.Code, attempt)",source);
        Assert.Contains("AfterConfirmation(confirmation, measurement, attempt, frame.SaturationLevel)",source);
        Assert.Contains("blendExposureIncreased,",source);
        Assert.Contains("shortBlendExposureIncreases",source);
        Assert.Contains("AfterUnmeasured(measurement, attempt", source);
        Assert.Contains("G3ShortExposurePolicy.RecognitionPeak(frame", source);
        Assert.Contains("field.Image?.MetaData.Image.ExposureTime * 1000", source);
        Assert.Contains("previous?.Gate.Disposition != GateDisposition.Passed", source);
        Assert.Contains("signalExposureIncreased,", source);
        Assert.Contains("if (exposureChanged)",source);
        Assert.Contains("previousHash = previousFramePath = previousReceipt = null",source);
        Assert.Contains("nextExposurePolicy.MaximumFrames",source);
        Assert.Contains("const int shortGain = 0",source);
        Assert.Contains("ValidateG3SolveProbeImage(capture, image, exposure.Value",source);
        Assert.DoesNotContain("SetExposure",source);
        Assert.DoesNotContain("SetGain",source);
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
        Assert.Contains("currentResidual + currentField.TargetIdentification.CatalogPositionSpreadPixels <= Phd2HandoffResidualPixels(currentField, placementPreset)",runner);
        Assert.Contains("coarseResidualPixels + lastG3Field.TargetIdentification.CatalogPositionSpreadPixels",runner);
        var handoff=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Sources","RealObservationStageRunner.Handoff.cs"));
        Assert.Contains("field.TargetIdentification.CatalogPositionSpreadPixels, Phd2HandoffResidualPixels(field, preset)",handoff);
        Assert.Contains("attempts.Count == 0 ? \"G3_SEARCH_NOT_STARTED_RETURNED\"",runner);
    }
}
