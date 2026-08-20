using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Read-only binding and quality policy for the C11 focal plane seen by G3.
/// This policy deliberately exposes no motion operation: only N.I.N.A.'s
/// Star Focuser Pro/Gemini owner may change this focus, and an observation
/// sequence pauses for operator intervention when the metric is not valid.
/// </summary>
internal static class C11MainFocusPolicy
{
    public static GateResult ValidateOwner(C11MainFocusOwnerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Connected)
        {
            return GateResult.Unknown(
                "C11_MAIN_FOCUSER_NOT_CONNECTED",
                "N.I.N.A. does not report its C11 Star Focuser Pro owner as connected.");
        }
        if (!string.Equals(
                snapshot.DeviceId,
                FocusDomainConventions.C11LogicalDeviceId,
                StringComparison.Ordinal))
        {
            return GateResult.Fail(
                "C11_MAIN_FOCUSER_IDENTITY_MISMATCH",
                $"N.I.N.A. focuser DeviceId '{snapshot.DeviceId}' does not match the locked C11 owner '{FocusDomainConventions.C11LogicalDeviceId}'.");
        }
        if (snapshot.PositionSteps < 0)
        {
            return GateResult.Unknown(
                "C11_MAIN_FOCUSER_POSITION_UNAVAILABLE",
                $"N.I.N.A. Star Focuser Pro returned the non-attestable position {snapshot.PositionSteps} steps.");
        }

        return GateResult.Pass(
            "C11_MAIN_FOCUSER_OWNER_VALID",
            $"N.I.N.A. owns {FocusDomainConventions.C11LogicalDeviceId} at {snapshot.PositionSteps} steps; this check issued no focus motion.",
            new Dictionary<string, double> { ["positionSteps"] = snapshot.PositionSteps });
    }

    public static GateResult ValidateLockedPosition(
        C11MainFocusOwnerSnapshot snapshot,
        NightSetupRecord setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var ownerGate = ValidateOwner(snapshot);
        if (ownerGate.Disposition != GateDisposition.Passed) return ownerGate;

        var bindings = setup.FocusDomains?
            .Where(binding => binding.Role == FocusDomainRole.C11Main)
            .ToArray() ?? [];
        if (bindings.Length != 1)
        {
            return GateResult.Unknown(
                "C11_MAIN_FOCUSER_LOCK_MISSING",
                $"The locked Night Setup contains {bindings.Length} C11 main-focus bindings; exactly one is required.");
        }

        var expected = bindings[0];
        if (snapshot.PositionSteps != expected.StartPositionSteps)
        {
            return GateResult.Fail(
                "C11_MAIN_FOCUSER_POSITION_MISMATCH",
                $"N.I.N.A. Star Focuser Pro is at {snapshot.PositionSteps} steps, but the locked Night Setup requires {expected.StartPositionSteps} steps. No automatic focus motion was issued.",
                new Dictionary<string, double>
                {
                    ["positionSteps"] = snapshot.PositionSteps,
                    ["lockedPositionSteps"] = expected.StartPositionSteps,
                });
        }

        return GateResult.Pass(
            "C11_MAIN_FOCUSER_LOCK_MATCHED",
            $"N.I.N.A. Star Focuser Pro position matches the locked C11/Gemini main-focus position {snapshot.PositionSteps} steps.",
            new Dictionary<string, double> { ["positionSteps"] = snapshot.PositionSteps });
    }

    public static GateResult ToObservationGate(G3StellarFocusMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        if (measurement.Gate.Disposition == GateDisposition.Passed)
        {
            return GateResult.Pass(
                "G3_MAIN_FOCUS_VERIFIED",
                $"G3 verified the C11/Gemini main focus with {measurement.StarCount} robust stars: median FWHM {measurement.MedianFwhmPixels:F2} px, ellipticity {measurement.MedianEllipticity:F3}, confidence {measurement.Confidence:F3}.",
                MeasurementMetrics(measurement));
        }

        return GateResult.Unknown(
            "G3_MAIN_FOCUS_UNVERIFIED",
            $"G3 cannot verify the C11 main focus ({measurement.Gate.Code}: {measurement.Gate.Message}). " +
            "Only N.I.N.A.'s Star Focuser Pro controlling the physical Gemini focuser on COM8 can correct these pre-slit stars; UVEX M2 and the GS350 ToupTek AAF are different optical paths and are prohibited substitutes.",
            MeasurementMetrics(measurement));
    }

    public static IReadOnlyDictionary<string, double> MeasurementMetrics(
        G3StellarFocusMeasurement measurement) => new Dictionary<string, double>
    {
        ["medianFwhmPixels"] = measurement.MedianFwhmPixels,
        ["medianEllipticity"] = measurement.MedianEllipticity,
        ["starCount"] = measurement.StarCount,
        ["detectedStarCount"] = measurement.DetectedStarCount,
        ["saturatedStarFraction"] = measurement.SaturatedStarFraction,
        ["medianSignalToNoise"] = measurement.MedianSignalToNoise,
        ["relativeFwhmMad"] = measurement.RelativeFwhmMad,
        ["confidence"] = measurement.Confidence,
    };
}

internal sealed record C11MainFocusOwnerSnapshot(
    bool Connected,
    string DeviceId,
    int PositionSteps,
    string? Name,
    string? DisplayName,
    string? DriverInfo,
    string? DriverVersion,
    DateTimeOffset ReadUtc);
