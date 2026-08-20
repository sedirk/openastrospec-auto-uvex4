using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class C11MainFocusPolicyTests
{
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
