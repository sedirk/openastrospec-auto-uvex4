using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class C11MainFocusPolicyTests
{
    [Fact]
    public void NewRunCapturesCurrentFocusButResumeAndOtherChecksCannotRebaseIt()
    {
        var setup = Setup();
        var before = System.Text.Json.JsonSerializer.Serialize(setup);
        var run = new C11RunFocusLock();
        var current = Snapshot(true, FocusDomainConventions.C11LogicalDeviceId, 4950);
        Assert.Equal("C11_MAIN_FOCUSER_RUN_LOCK_MISSING", run.Validate(current, setup).Code);
        Assert.Equal(GateDisposition.Passed, run.CaptureOrValidate(current, setup).Disposition);
        Assert.Equal(4950, run.Position);
        Assert.Equal(GateDisposition.Passed, run.Validate(current, setup).Disposition);
        Assert.Equal(GateDisposition.Failed, run.CaptureOrValidate(current with { PositionSteps = 4900 }, setup).Disposition);
        Assert.Equal(GateDisposition.Failed, run.Validate(current with { PositionSteps = 5000 }, setup).Disposition);
        Assert.Equal(4950, run.Position);
        Assert.Equal(GateDisposition.Passed, new C11RunFocusLock().CaptureOrValidate(current with { PositionSteps = 4900 }, setup).Disposition);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(setup));
    }

    [Theory]
    [InlineData(false, false, 4950)]
    [InlineData(true, true, 4950)]
    [InlineData(true, false, -1)]
    [InlineData(true, false, 10001)]
    public void UntrustedOwnerCannotInitializeRunLock(bool connected, bool moving, int position)
    {
        var run = new C11RunFocusLock();
        Assert.NotEqual(GateDisposition.Passed, run.CaptureOrValidate(Snapshot(connected, FocusDomainConventions.C11LogicalDeviceId, position) with { IsMoving = moving }, Setup()).Disposition);
        Assert.Null(run.Position);
        Assert.NotEqual(GateDisposition.Passed, run.CaptureOrValidate(Snapshot(true, "wrong-owner", 4950), Setup()).Disposition);
        Assert.Null(run.Position);
    }

    private static NightSetupRecord Setup() => new(
        2, "fixture", DateTimeOffset.UtcNow, 2, 15, 0, 500, 1000, 5000,
        new("atr", 100, 256, 1, 1, -10, "fixture", 0, 0, 100, 100), "g3", "phd",
        new("qhy", 0, 0, 1, 1, null, "fixture", 0, 0, 100, 100),
        DispersionDirection.BlueAtLeftRedAtRight, 400, 700, CalibrationStrategy.CompactEmissionLineObject,
        "fixture", new HorizonPolicy(), "fixture", FocusDomains: [new(
            FocusDomainRole.C11Main, FocusDomainConventions.C11Owner, FocusDomainConventions.C11LogicalDeviceId,
            new(FocusMechanism.Gemini, "fixture", "fixture", null), 5000,
            new(0, 10000, 0, 0, FocusApproachDirection.None, 0),
            new(FocusMetricKind.G3StellarShape, "g3", 4, "px", new string('a', 64)), DateTimeOffset.UtcNow, null, .9)]);

    [Fact]
    public void OwnerRequiresConnectedExactStarFocuserAndAttestablePosition()
    {
        var disconnected = Snapshot(false, FocusDomainConventions.C11LogicalDeviceId, 1234);
        var wrongDevice = Snapshot(true, "ASCOM.ToupTek.AAF", 1234);
        var missingPosition = Snapshot(true, FocusDomainConventions.C11LogicalDeviceId, -1);
        var valid = Snapshot(true, FocusDomainConventions.C11LogicalDeviceId, 1234);

        Assert.Equal(GateDisposition.Indeterminate, C11MainFocusPolicy.ValidateOwner(disconnected).Disposition);
        Assert.Equal("C11_MAIN_FOCUSER_IDENTITY_MISMATCH", C11MainFocusPolicy.ValidateOwner(wrongDevice).Code);
        Assert.Equal("C11_MAIN_FOCUSER_POSITION_UNAVAILABLE", C11MainFocusPolicy.ValidateOwner(missingPosition).Code);
        Assert.Equal(GateDisposition.Passed, C11MainFocusPolicy.ValidateOwner(valid).Disposition);
    }

    [Fact]
    public void FailedG3MetricPausesWithCorrectOpticalOwnerAndNoSubstitution()
    {
        var measurement = new G3StellarFocusMeasurement(
            GateResult.Unknown("G3_FOCUS_STARS_TOO_BROAD", "synthetic broad stars"),
            MedianFwhmPixels: 21.5,
            MedianEllipticity: 0.42,
            StarCount: 5,
            DetectedStarCount: 7,
            SaturatedStarFraction: 0,
            MedianSignalToNoise: 15,
            RelativeFwhmMad: 0.2,
            Confidence: 0.31,
            Stars: Array.Empty<StarCandidate>());

        var gate = C11MainFocusPolicy.ToObservationGate(measurement);

        Assert.Equal(GateDisposition.Indeterminate, gate.Disposition);
        Assert.Equal("G3_MAIN_FOCUS_UNVERIFIED", gate.Code);
        Assert.Contains("Star Focuser Pro", gate.Message, StringComparison.Ordinal);
        Assert.Contains("Gemini", gate.Message, StringComparison.Ordinal);
        Assert.Contains("COM8", gate.Message, StringComparison.Ordinal);
        Assert.Contains("UVEX M2", gate.Message, StringComparison.Ordinal);
        Assert.Contains("ToupTek AAF", gate.Message, StringComparison.Ordinal);
        Assert.Equal(21.5, gate.Metrics!["medianFwhmPixels"]);
        Assert.Equal(5, gate.Metrics["starCount"]);
        Assert.Equal(0.31, gate.Metrics["confidence"]);
    }

    [Fact]
    public void MainFocusPolicyProvidesNoMotionApi()
    {
        Assert.DoesNotContain(
            typeof(C11MainFocusPolicy).GetMethods(),
            method => method.Name.Contains("Move", StringComparison.OrdinalIgnoreCase));
    }

    private static C11MainFocusOwnerSnapshot Snapshot(bool connected, string deviceId, int position) => new(
        connected,
        deviceId,
        position,
        "Star Focuser Pro",
        "Star Focuser Pro",
        "Gemini",
        "6.6.0.0",
        DateTimeOffset.UtcNow);
}
