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
