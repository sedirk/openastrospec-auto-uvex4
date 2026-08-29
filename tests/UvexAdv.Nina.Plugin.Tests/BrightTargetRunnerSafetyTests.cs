using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class BrightTargetRunnerSafetyTests
{
    private static readonly string Source = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.cs"));

    [Fact]
    public void ExceptionalBranchUsesExactConfiguredExposureAndIndependentAuthorityBeforeAcceptance()
    {
        var body = Slice(
            "private async Task<G3FieldState?> TryAcquireBrightTargetFromWingsAsync(",
            "private GateResult ValidateBrightTargetG3Image(");

        Assert.Contains("if (!branch.Enabled) return null", body, StringComparison.Ordinal);
        Assert.Contains("branch.MinimumG3ExposureMilliseconds", body, StringComparison.Ordinal);
        Assert.Contains("RequireImmediatePhysicalActionGatesAsync", body, StringComparison.Ordinal);
        Assert.Contains("BrightTargetAuthorityGate.Evaluate", body, StringComparison.Ordinal);
        Assert.Contains("BrightTargetWingCentroidAnalyzer.Analyze", body, StringComparison.Ordinal);
        Assert.Contains("G3FrameUsedForFocus: false", body, StringComparison.Ordinal);
        Assert.Contains("focus.Metric.EvidenceSha256", body, StringComparison.Ordinal);
        Assert.Contains("lastQhySolve.EvidenceSha256", body, StringComparison.Ordinal);
        Assert.Contains("ComputeFileSha256Async", body, StringComparison.Ordinal);
        Assert.Contains("BRIGHT_TARGET_QHY_EVIDENCE_HASH_MISMATCH", body, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!shortSolve.Result.Success)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MountTransform", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplySkyCorrection", body, StringComparison.Ordinal);

        var checkpoint = body.IndexOf("RequireImmediatePhysicalActionGatesAsync", StringComparison.Ordinal);
        var qhyRehash = body.IndexOf("currentAcceptedQhySha256 = await ComputeFileSha256Async", StringComparison.Ordinal);
        var exposure = body.IndexOf("CaptureG3FullFrameForAcquisitionAsync", StringComparison.Ordinal);
        var authority = body.IndexOf("BrightTargetAuthorityGate.Evaluate", StringComparison.Ordinal);
        var pass = body.IndexOf("G3_BRIGHT_TARGET_FIELD_IDENTIFIED", StringComparison.Ordinal);
        Assert.True(qhyRehash >= 0 && qhyRehash < checkpoint);
        Assert.True(checkpoint >= 0 && checkpoint < exposure);
        Assert.True(authority >= 0 && authority < pass);
    }

    [Fact]
    public void BrightTargetEvidenceStatesThatSaturatedFrameIsNotFocusOrOpticalOffsetEvidence()
    {
        var body = Slice(
            "private Task<string> PublishBrightTargetEvidenceAsync(",
            "private async Task<G3SlitIlluminationSequence> CaptureG3SlitIlluminationSequenceAsync(");

        Assert.Contains("focusEligible = false", body, StringComparison.Ordinal);
        Assert.Contains("currentSaturatedFrameWasUsedForFocus = false", body, StringComparison.Ordinal);
        Assert.Contains("targetSpecificConstant = (string?)null", body, StringComparison.Ordinal);
        Assert.Contains("opticalAxisOffset = (string?)null", body, StringComparison.Ordinal);
        Assert.Contains("g3PlateSolveMayFail = true", body, StringComparison.Ordinal);
        Assert.Contains("acceptedQhy.Sha256", body, StringComparison.Ordinal);
        Assert.Contains("qhySolve.EvidenceSha256", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Enif", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Deneb", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BranchIsDisabledAndUnconfiguredUntilCommissioningExplicitlyEnablesIt()
    {
        var disabled = BrightTargetRunConfiguration.Disabled;

        Assert.False(disabled.Enabled);
        Assert.Equal(0, disabled.MinimumG3ExposureMilliseconds);
        Assert.Empty(disabled.Validate(normalG3ExposureMilliseconds: 10_000));

        var invalidEnabled = disabled with { Enabled = true };
        var issues = invalidEnabled.Validate(normalG3ExposureMilliseconds: 10_000);
        Assert.NotEmpty(issues);
        Assert.Contains(issues, issue => issue.Contains("minimum G3 exposure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidConfigurationContainsNoTargetCoordinateOrOpticalAxisTerm()
    {
        var configured = new BrightTargetRunConfiguration(
            Enabled: true,
            MinimumG3ExposureMilliseconds: 125,
            MaximumQhyWcsAge: TimeSpan.FromMinutes(5),
            MaximumG3FrameAge: TimeSpan.FromMinutes(2),
            MaximumQhyTargetResidualArcseconds: 20,
            MaximumCatalogCoordinateMismatchArcseconds: 1,
            MinimumC11FocusConfidence: 0.7,
            CentroidOptions: new BrightTargetCentroidOptions());

        Assert.Empty(configured.Validate(normalG3ExposureMilliseconds: 10_000));
        var names = typeof(BrightTargetRunConfiguration).GetProperties().Select(property => property.Name).ToArray();
        Assert.DoesNotContain(names, name => name.Contains("Offset", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("RightAscension", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Declination", StringComparison.OrdinalIgnoreCase));
    }

    private static string Slice(string startMarker, string endMarker)
    {
        var start = Source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = Source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not locate source section {startMarker} -> {endMarker}.");
        return Source[start..end];
    }
}
