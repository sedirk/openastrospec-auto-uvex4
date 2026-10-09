using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3HandoffBoundaryRecheckTests
{
    [Fact]
    public void RecordedV1770CygBorderlinePairGetsTwoImagesNotAnotherMove()
    {
        const double residual = 42.2969628477, spread = 3.8284373818, radius = 44;
        Assert.True(G3HandoffBoundaryRecheckPolicy.ShouldRecheck(true, true, false,
            residual, spread, radius, 4));
        Assert.True(G3WcsRecoveryPolicy.NeedsCoarseCentering(GateResult.Pass("measured", ""),
            true, residual + spread, radius)); // The old pair is NOT authorized.
        Assert.False(G3HandoffBoundaryRecheckPolicy.ShouldRecheck(true, true, true,
            residual, spread, radius, 4));
        Assert.False(G3WcsRecoveryPolicy.CanRetryNearTargetResponse(true, true, true, true,
            residual, 101.1400988913, spread, 1.3064728884, .3848065563, 2, 100, 0));
    }

    [Theory]
    [InlineData(42, 1, 44, 4)] // Already fits: do not delay handoff.
    [InlineData(45, 1, 44, 4)] // Centre itself is outside: not marginal scatter.
    [InlineData(42, 3, 44, 5)] // No room for a whole new pair.
    [InlineData(42, 3, 44, 1)]
    [InlineData(double.NaN, 3, 44, 4)]
    [InlineData(42, double.PositiveInfinity, 44, 4)]
    [InlineData(42, -1, 44, 4)]
    [InlineData(42, 3, double.NaN, 4)]
    public void NonMarginalOrInvalidEvidenceCannotExtendWindow(double residual, double spread, double radius, int frames) =>
        Assert.False(G3HandoffBoundaryRecheckPolicy.ShouldRecheck(true, true, false, residual, spread, radius, frames));

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IdentityAndSlitMustAlreadyPass(bool confirmed, bool slitMeasured) =>
        Assert.False(G3HandoffBoundaryRecheckPolicy.ShouldRecheck(confirmed, slitMeasured, false, 42, 3, 44, 4));

    private static G3ShortPositionMeasurement Measured(double x, double y) => new(
        GateResult.Pass("G3_SEP_MEASURED", ""),
        new(GateResult.Pass("TARGET_IDENTIFIED", ""), new(new(x, y), 5100, 10000, 50, 3, .1, 0, 100),
            new(789.1206, 432.2813), 16, 2), true, .5);

    [Fact]
    public void NewPairMustIndependentlyConfirmAndStillFitUnchangedHandoff()
    {
        // Synthetic future frames, NOT a claim about unseen real-sky images.
        var first = Measured(776, 423);
        var second = Measured(776.5, 423.5);
        var a = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, first, null, new('a', 64), 5, 100, 6);
        Assert.False(a.Accepted);
        Assert.True(a.RetryAllowed);
        var b = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first, second, new('a', 64), new('b', 64), 6, 100, 6);
        Assert.True(b.Accepted);
        var residual = Math.Sqrt(Math.Pow(817.5477 - 776.5, 2) + Math.Pow(429.0063 - 423.5, 2));
        Assert.False(G3WcsRecoveryPolicy.NeedsCoarseCentering(b.Gate, true, residual + b.PositionSpreadPixels, 44));
        Assert.False(G3HandoffBoundaryRecheckPolicy.ShouldRecheck(true, true, true, residual, b.PositionSpreadPixels, 44, 6));
    }

    [Fact]
    public void NewPairFailureCannotFallBackToOldAcceptedPositionOrExtendAgain()
    {
        var first = Measured(776, 423);
        var bad = Measured(784, 429);
        var b = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first, bad, new('a', 64), new('b', 64), 6, 100, 6);
        Assert.False(b.Accepted);
        Assert.False(b.RetryAllowed);
        var duplicate = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(first, first, new('a', 64), new('a', 64), 6, 100, 6);
        Assert.False(duplicate.Accepted);
        Assert.False(duplicate.RetryAllowed);
    }

    [Fact]
    public void ProductionBranchKeepsSameLoopOriginalBindingsAndBudgetedOwnerCapture()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.CatalogShortPosition.cs"));
        var start = source.IndexOf("if (G3HandoffBoundaryRecheckPolicy.ShouldRecheck", StringComparison.Ordinal);
        var end = source.IndexOf("\"TARGET_BRANCH_PASSED\"", start, StringComparison.Ordinal);
        var branch = source[start..end];
        Assert.Contains("Phd2HandoffResidualPixels(field with { TargetIdentification = identification }, preset)", source);
        Assert.Contains("handoffBoundaryRecheckUsed = true", branch);
        Assert.Contains("MaximumFrames = attempt + 2", branch);
        Assert.Contains("previous = null", branch);
        Assert.Contains("previousHash = previousFramePath = previousReceipt = null", branch);
        Assert.Contains("continue;", branch);
        Assert.Contains("handoffBoundaryRecheckUsed ? exposurePolicy", source);
        Assert.Contains("handoffBoundaryRecheckUsed ? nextExposurePolicy", source);
        Assert.Contains("!handoffBoundaryRecheckUsed && previous?.Gate.Disposition", source);
        Assert.DoesNotContain("RefineClippedCatalogPositionAsync(", branch); // No recursive fresh budget.
        Assert.DoesNotContain("return field", branch);
        Assert.DoesNotContain("SlewTo", source);
        Assert.DoesNotContain("SetExactLock", source);
        Assert.DoesNotContain("SolveImageAsync", source);
        Assert.Contains("ValidateG3FieldMountBindingForMotionAsync(context, field", source);
        Assert.Contains("CaptureG3NativeSingleFrameForAcquisitionAsync", source);
        var runner = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        Assert.Contains("=> await RunWithG3PendingReturnTimeAsync(\"capture\"", runner);
    }
}
