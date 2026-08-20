using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class SlitWheelIdentityTests
{
    [Fact]
    public void Match_ConfirmsReportedPhysicalSlot()
    {
        var result = SlitWheelIdentityMatcher.Match(
            Calibration(), Measurement(9.1, 0.4), 2, 15, "g3", 1, 1, 1920, 1080);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("SLIT_LED_IDENTITY_MATCHED", result.Gate.Code);
        Assert.Equal(2, result.MatchedCandidate?.WheelPosition);
    }

    [Fact]
    public void Match_BlocksWhenOpticalWidthIdentifiesDifferentSlot()
    {
        var result = SlitWheelIdentityMatcher.Match(
            Calibration(), Measurement(15.1, 0.4), 2, 15, "g3", 1, 1, 1920, 1080);

        Assert.Equal(GateDisposition.Failed, result.Gate.Disposition);
        Assert.Equal("SLIT_LED_IDENTITY_POSITION_MISMATCH", result.Gate.Code);
        Assert.Equal(3, result.MatchedCandidate?.WheelPosition);
        Assert.Contains("installation", result.Gate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Match_DoesNotGuessAnOutOfFamilyWidth()
    {
        var result = SlitWheelIdentityMatcher.Match(
            Calibration(), Measurement(40, 0.4), 2, 15, "g3", 1, 1, 1920, 1080);

        Assert.Equal(GateDisposition.Failed, result.Gate.Disposition);
        Assert.Equal("SLIT_LED_IDENTITY_OUT_OF_FAMILY", result.Gate.Code);
    }

    [Fact]
    public void Match_RequiresPassingFreshLedGeometry()
    {
        var measurement = Measurement(9, 0.4) with
        {
            Gate = GateResult.Unknown("NO_SIGNAL", "No line")
        };

        var result = SlitWheelIdentityMatcher.Match(
            Calibration(), measurement, 2, 15, "g3", 1, 1, 1920, 1080);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("SLIT_LED_IDENTITY_GEOMETRY_UNAVAILABLE", result.Gate.Code);
    }

    [Fact]
    public void Match_RejectsDifferentDetectorGeometry()
    {
        var result = SlitWheelIdentityMatcher.Match(
            Calibration(), Measurement(9, 0.4), 2, 15, "g3", 2, 2, 960, 540);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("SLIT_LED_IDENTITY_DETECTOR_MISMATCH", result.Gate.Code);
    }

    [Fact]
    public void Calibration_RequiresFourIndependentEmpiricalEntries()
    {
        var invalid = Calibration() with
        {
            Fingerprints = Calibration().Fingerprints.Take(1).ToArray(),
            CalibrationSha256 = string.Empty,
        };
        invalid = invalid.WithComputedSha256();

        Assert.Contains(invalid.Validate(), issue => issue.Contains("exactly 4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Calibration_RejectsFingerprintsThatCannotBeDistinguished()
    {
        var original = Calibration();
        var entries = original.Fingerprints.ToArray();
        entries[2] = entries[2] with { MeasuredWidthPixels = 9.5 };
        var invalid = (original with { Fingerprints = entries, CalibrationSha256 = string.Empty }).WithComputedSha256();

        Assert.Contains(invalid.Validate(), issue => issue.Contains("insufficient", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Calibration_RejectsSwappedPhysicalInstallationEvenWhenLabelsFollowDeclaredOrdinals()
    {
        var original = Calibration();
        var entries = original.Fingerprints.ToArray();
        entries[1] = entries[1] with { MeasuredWidthPixels = 15 };
        entries[2] = entries[2] with { MeasuredWidthPixels = 9 };
        var invalid = (original with { Fingerprints = entries, CalibrationSha256 = string.Empty }).WithComputedSha256();

        Assert.Contains(invalid.Validate(), issue =>
            issue.Contains("order contradicts", StringComparison.OrdinalIgnoreCase) &&
            issue.Contains("swapped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Calibration_ContentHashChangesWithPhysicalMapping()
    {
        var original = Calibration();
        var entries = original.Fingerprints.ToArray();
        entries[1] = entries[1] with { WheelPosition = 3 };
        entries[2] = entries[2] with { WheelPosition = 2 };
        var changed = (original with { Fingerprints = entries, CalibrationSha256 = string.Empty }).WithComputedSha256();

        Assert.NotEqual(original.CalibrationSha256, changed.CalibrationSha256);
    }

    private static SlitWheelIdentityCalibration Calibration()
    {
        var entries = new[]
        {
            Fingerprint(1, "300um", 300, 70, 1.0),
            Fingerprint(2, "15um", 15, 9, 0.6),
            Fingerprint(3, "25um", 25, 15, 0.7),
            Fingerprint(4, "35um", 35, 22, 0.8),
        };
        return new SlitWheelIdentityCalibration(
            SlitWheelIdentityCalibration.CurrentSchemaVersion,
            "slit-wheel-identity-1",
            "install-epoch-1",
            "g3",
            1,
            1,
            1920,
            1080,
            3,
            2,
            entries,
            string.Empty).WithComputedSha256();
    }

    private static SlitWidthFingerprint Fingerprint(
        int position,
        string label,
        double nominalMicrometers,
        double pixels,
        double uncertainty) =>
        new(
            position,
            label,
            nominalMicrometers,
            pixels,
            uncertainty,
            DateTimeOffset.Parse("2026-08-18T12:00:00Z"),
            new string((char)('A' + position - 1), 64),
            SlitDarkApertureResolution.DirectTwoEdge,
            pixels / 2,
            0.1,
            new string((char)('0' + position), 64),
            new string((char)('5' + position), 64));

    private static SlitDarkApertureHdrAnalysis Measurement(double width, double uncertainty) =>
        new(
            GateResult.Pass("SLIT_DARK_APERTURE_DIRECTLY_MEASURED", "measured"),
            new SlitGeometry("fresh", new PixelPoint(800, 400), 0, 400, width, uncertainty, "g3", 1, 1),
            new SlitGeometry("reflection", new PixelPoint(800, 400 - width / 2), 0, 400, 3, uncertainty, "g3", 1, 1),
            SlitDarkApertureResolution.DirectTwoEdge,
            width,
            uncertainty,
            width / 2,
            0.1,
            20,
            0.01,
            0.2,
            0.8,
            500);
}
