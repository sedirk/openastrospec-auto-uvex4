using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3SolveProbeCapturePolicyTests
{
    [Fact]
    public void PHD2ResultWithUnsupportedPerRequestGainAndBinningUsesLockedProfileEvidence()
    {
        var captured = new Phd2SingleFrameResult(
            "probe.fit",
            UsedLoopSaveFallback: true,
            RequestedParametersApplied: false,
            DateTimeOffset.Parse("2026-08-19T12:00:00Z"));
        var profile = Profile(binning: 1, gain: 95);

        var gate = G3SolveProbeCapturePolicy.Validate(
            captured,
            fitsExposureMilliseconds: 10_000,
            fitsBinX: 1,
            fitsBinY: 1,
            fitsGain: 95,
            requestedExposureMilliseconds: 10_000,
            lockedBinning: 1,
            lockedGainPercent: 95,
            profile);

        Assert.True(captured.ExposureApplied);
        Assert.False(captured.RequestedParametersApplied);
        Assert.False(captured.GainAndBinningApplied);
        Assert.Equal(UvexAdv.Observatory.GateDisposition.Passed, gate.Disposition);
        Assert.Equal("G3_SOLVE_PROBE_FRAME_VALID", gate.Code);
    }

    [Fact]
    public void ExposedFitsGainMismatchFailsClosed()
    {
        var captured = new Phd2SingleFrameResult(
            "probe.fit",
            true,
            false,
            DateTimeOffset.Parse("2026-08-19T12:00:00Z"));

        var gate = G3SolveProbeCapturePolicy.Validate(
            captured,
            5_000,
            1,
            1,
            fitsGain: 60,
            requestedExposureMilliseconds: 5_000,
            lockedBinning: 1,
            lockedGainPercent: 95,
            Profile(1, 95));

        Assert.Equal(UvexAdv.Observatory.GateDisposition.Failed, gate.Disposition);
        Assert.Equal("G3_SOLVE_PROBE_GAIN_MISMATCH", gate.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)] // Missing gain header still requires the native owner's attestation.
    public void NativeMinimumGainFrameKeepsOriginalHighGainProfileBinding(int fitsGain)
    {
        var captured = new Phd2SingleFrameResult("short.fit", false, true, DateTimeOffset.UtcNow,
            VerifiedExposureMilliseconds: 10);
        var profile = Profile(1, 100);
        var gate = G3SolveProbeCapturePolicy.Validate(captured, 10, 1, 1, fitsGain,
            10, 1, 100, profile, nativeCaptureGainPercent: 0);

        Assert.Equal(UvexAdv.Observatory.GateDisposition.Passed, gate.Disposition);
        Assert.Equal(100, profile.GainPercent);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MinimumGainOverrideCannotUseProfileOrLoopSaveFallback(bool loopSave, bool nativeApplied)
    {
        var captured = new Phd2SingleFrameResult("short.fit", loopSave, nativeApplied,
            DateTimeOffset.UtcNow, VerifiedExposureMilliseconds: 10);
        var gate = G3SolveProbeCapturePolicy.Validate(captured, 10, 1, 1, 0,
            10, 1, 100, Profile(1, 100), nativeCaptureGainPercent: 0);

        Assert.Equal("G3_SOLVE_PROBE_NATIVE_GAIN_UNATTESTED", gate.Code);
        Assert.NotEqual(UvexAdv.Observatory.GateDisposition.Passed, gate.Disposition);
    }

    [Fact]
    public void NativeMinimumGainOverrideRejectsFITSStillReportingGuidingGain()
    {
        var captured = new Phd2SingleFrameResult("short.fit", false, true, DateTimeOffset.UtcNow,
            VerifiedExposureMilliseconds: 10);
        var gate = G3SolveProbeCapturePolicy.Validate(captured, 10, 1, 1, 100,
            10, 1, 100, Profile(1, 100), nativeCaptureGainPercent: 0);

        Assert.Equal("G3_SOLVE_PROBE_GAIN_MISMATCH", gate.Code);
        Assert.Equal(UvexAdv.Observatory.GateDisposition.Failed, gate.Disposition);
    }

    [Fact]
    public void NativeGainDoesNotAuthorizeSubstitutingAnUnboundGuidingProfile()
    {
        var captured = new Phd2SingleFrameResult("short.fit", false, true, DateTimeOffset.UtcNow,
            VerifiedExposureMilliseconds: 10);
        var gate = G3SolveProbeCapturePolicy.Validate(captured, 10, 1, 1, 0,
            10, 1, 100, Profile(1, 0), nativeCaptureGainPercent: 0);

        Assert.Equal("G3_SOLVE_PROBE_PROFILE_PARAMETERS_UNATTESTED", gate.Code);
    }

    private static Phd2ProfileBindingSnapshot Profile(int binning, int gain) => new(
        ProfileId: 3,
        ProfileName: "G3",
        CameraName: "G3M2210M",
        CameraStableIds: ["USB#G3"],
        MountName: "ASCOM",
        Binning: binning,
        GainPercent: gain,
        FocalLengthMillimeters: 2800,
        CameraBitsPerPixel: 12,
        EvidenceSource: @"HKCU\Software\StarkLabs\PHDGuidingV2\profile\3",
        Sha256: new string('A', 64),
        CapturedUtc: DateTimeOffset.Parse("2026-08-19T11:00:00Z"));
}
