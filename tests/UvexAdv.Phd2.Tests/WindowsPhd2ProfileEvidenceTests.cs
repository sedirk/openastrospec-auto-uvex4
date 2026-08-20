using UvexAdv.Phd2;

namespace UvexAdv.Phd2.Tests;

public sealed class WindowsPhd2ProfileEvidenceTests
{
    private const string StableId = @"\\?\usb#vid_0547&pid_14ab#fixture-current";

    [Fact]
    public void ExactPersistedProfileBindingPassesWithoutOpeningCamera()
    {
        var evidence = Snapshot([StableId]);

        var result = WindowsPhd2ProfileEvidence.Validate(Requirement(), evidence);

        Assert.Equal(Phd2ValidationStatus.Valid, result.Status);
        Assert.Empty(result.Failures);
        Assert.Empty(result.IndeterminateReasons);
    }

    [Fact]
    public void DifferentUsbInstanceIsRejected()
    {
        var evidence = Snapshot([@"\\?\usb#vid_0547&pid_14ab#fixture-wrong"]);

        var result = WindowsPhd2ProfileEvidence.Validate(Requirement(), evidence);

        Assert.Equal(Phd2ValidationStatus.Invalid, result.Status);
        Assert.Contains(result.Failures, item => item.Contains("camera instance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MultiplePersistedCameraBindingsAreIndeterminate()
    {
        var evidence = Snapshot([StableId, @"\\?\usb#vid_0547&pid_14ab#fixture-historical"]);

        var result = WindowsPhd2ProfileEvidence.Validate(Requirement(), evidence);

        Assert.Equal(Phd2ValidationStatus.Indeterminate, result.Status);
        Assert.Contains(result.IndeterminateReasons, item => item.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProfileGainAndBinningMustMatchCommissionedValues()
    {
        var evidence = Snapshot([StableId]) with { Binning = 2, GainPercent = 50 };

        var result = WindowsPhd2ProfileEvidence.Validate(Requirement(), evidence);

        Assert.Equal(Phd2ValidationStatus.Invalid, result.Status);
        Assert.Contains(result.Failures, item => item.Contains("binning", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Failures, item => item.Contains("gain", StringComparison.OrdinalIgnoreCase));
    }

    private static Phd2ProfileBindingRequirement Requirement() => new(
        2,
        "c11+ccdt67+slit+2210",
        "ToupTek Camera",
        StableId,
        "OnStep Telescope (ASCOM)",
        Binning: 1,
        GainPercent: 100);

    private static Phd2ProfileBindingSnapshot Snapshot(IReadOnlyList<string> stableIds) => new(
        2,
        "c11+ccdt67+slit+2210",
        "ToupTek Camera",
        stableIds,
        "OnStep Telescope (ASCOM)",
        1,
        100,
        2150,
        16,
        @"HKCU\Software\StarkLabs\PHDGuidingV2\profile\2",
        new string('A', 64),
        DateTimeOffset.UtcNow);
}
