using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyTelescope;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class StartupGuideStationaryMountTests
{
    // Read-only owner samples at 2026-10-30 15:05:30.568Z and 15:05:36.700Z.
    // Not synthetic sky success: these attest only a stopped untracked mount.
    internal static StartupGuideMountReadback RecordedBefore => new("mount", true, false, false, false,
        "pierWest", "JNOW", 25.206616249999996, 21.501887777777778, 78.12027777777777, 182.36638888888888,
        DateTimeOffset.Parse("2026-10-30T15:05:28Z"), DateTimeOffset.Parse("2026-10-30T15:05:30.5684814Z"));
    internal static StartupGuideMountReadback RecordedAfter => RecordedBefore with
    {
        RaDegrees = 25.231657916666666, DeviceUtc = DateTimeOffset.Parse("2026-10-30T15:05:34Z"),
        ObservedUtc = DateTimeOffset.Parse("2026-10-30T15:05:36.7000629Z"),
    };

    [Fact]
    public void RecordedUntrackedMountIsStationaryButItsOldSkyFieldRemainsInvalidForMotion()
    {
        var before = RecordedBefore;
        var after = RecordedAfter;
        Assert.True(G3AcquisitionMotionPlanner.AngularSeparationArcseconds(before.RaDegrees, before.DecDegrees,
            after.RaDegrees, after.DecDegrees) > 80);
        Assert.Equal(GateDisposition.Passed, StartupGuideStationaryMount.Validate(before, after, "mount", 2).Disposition);
        var binding = Binding(before);
        Assert.Equal("G3_FIELD_MOUNT_BINDING_STALE", Motion(binding, after).Code);
        Assert.Equal(GateDisposition.Passed, Provenance(binding).Disposition);
        Assert.NotEqual(GateDisposition.Passed, Provenance(binding with { FrameSha256 = new string('E', 64) }).Disposition);
        Assert.NotEqual(GateDisposition.Passed, G3FieldMountBindingPolicy.ValidateProvenance(binding,
            "another-run", "action", "preset", binding.FramePath, binding.FrameSha256).Disposition);
        Assert.NotEqual(GateDisposition.Passed, G3FieldMountBindingPolicy.ValidateProvenance(binding,
            "run", "action", "preset", binding.FramePath, new string('F', 64)).Disposition);
    }

    [Fact]
    public void Reported160Point77ArcsecondSkyDriftDoesNotRequireTurningTrackingOn()
    {
        var before = RecordedBefore;
        var after = RecordedAfter with { RaDegrees = before.RaDegrees + 160.77 / (3600 * Math.Cos(before.DecDegrees * Math.PI / 180)) };
        Assert.Equal(GateDisposition.Passed, StartupGuideStationaryMount.Validate(before, after, "mount", 2).Disposition);
        Assert.Equal("G3_FIELD_MOUNT_BINDING_STALE", Motion(Binding(before), after).Code);
    }

    [Theory]
    [InlineData("moving")]
    [InlineData("pulse")]
    [InlineData("tracking-changed")]
    [InlineData("physical-displacement")]
    [InlineData("altitude-unavailable")]
    [InlineData("azimuth-unavailable")]
    [InlineData("epoch-changed")]
    [InlineData("pier-changed")]
    [InlineData("pier-unknown")]
    [InlineData("disconnected")]
    [InlineData("owner-changed")]
    [InlineData("cache-stale")]
    [InlineData("clock-future")]
    [InlineData("clock-regressed")]
    [InlineData("no-clock")]
    [InlineData("ra-invalid")]
    public void RealStopUncertaintyStillRejectsRetirement(string mode)
    {
        var after = mode switch
        {
            "moving" => RecordedAfter with { Slewing = true },
            "pulse" => RecordedAfter with { PulseGuiding = true },
            "tracking-changed" => RecordedAfter with { Tracking = true },
            "physical-displacement" => RecordedAfter with { AltitudeDegrees = RecordedAfter.AltitudeDegrees + .01 },
            "altitude-unavailable" => RecordedAfter with { AltitudeDegrees = double.NaN },
            "azimuth-unavailable" => RecordedAfter with { AzimuthDegrees = double.NaN },
            "epoch-changed" => RecordedAfter with { Epoch = "J2000" },
            "pier-changed" => RecordedAfter with { PierSide = "pierEast" },
            "pier-unknown" => RecordedAfter with { PierSide = "pierUnknown" },
            "disconnected" => RecordedAfter with { Connected = false },
            "owner-changed" => RecordedAfter with { DeviceId = "foreign" },
            "cache-stale" => RecordedAfter with { ObservedUtc = RecordedAfter.ObservedUtc.AddSeconds(15) },
            "clock-future" => RecordedAfter with { DeviceUtc = RecordedAfter.DeviceUtc.AddMinutes(1) },
            "clock-regressed" => RecordedAfter with { DeviceUtc = RecordedBefore.DeviceUtc.AddMilliseconds(-1) },
            "no-clock" => RecordedAfter with { DeviceUtc = default },
            _ => RecordedAfter with { RaDegrees = double.NaN },
        };
        Assert.Equal("GUIDE_STARTUP_STOP_UNCONFIRMED", StartupGuideStationaryMount.Validate(RecordedBefore, after, "mount", 2).Code);
    }

    [Fact]
    public void TrackingOnKeepsTheOriginalSameSkyRequirementRatherThanFixedAltitude()
    {
        var before = RecordedBefore with { Tracking = true };
        var after = RecordedAfter with { Tracking = true, RaDegrees = before.RaDegrees, AltitudeDegrees = before.AltitudeDegrees + .1 };
        Assert.Equal(GateDisposition.Passed, StartupGuideStationaryMount.Validate(before, after, "mount", 2).Disposition);
        Assert.NotEqual(GateDisposition.Passed, StartupGuideStationaryMount.Validate(before, after with { RaDegrees = before.RaDegrees + .01 }, "mount", 2).Disposition);
    }

    [Fact]
    public void StableClockOffsetDoesNotPreventTheLaterNormalClockRepairStage()
    {
        var before = RecordedBefore with { DeviceUtc = RecordedBefore.DeviceUtc.AddDays(-2) };
        var after = RecordedAfter with { DeviceUtc = RecordedAfter.DeviceUtc.AddDays(-2) };
        Assert.Equal(GateDisposition.Passed, StartupGuideStationaryMount.Validate(before, after, "mount", 2).Disposition);
    }

    [Fact]
    public void HorizontalAzimuthWrapIsNotMistakenForAMountJump()
    {
        var before = RecordedBefore with { AzimuthDegrees = 359.9999 };
        var after = RecordedAfter with { AzimuthDegrees = 0.0001 };
        Assert.Equal(GateDisposition.Passed, StartupGuideStationaryMount.Validate(before, after, "mount", 2).Disposition);
    }

    [Fact]
    public void OwnerSnapshotUsesDegreesAndUtcNotLocalTimeOrRounding()
    {
        var recorded = RecordedBefore;
        var info = new TelescopeInfo
        {
            DeviceId = "mount", Connected = true, TrackingEnabled = false, SideOfPier = PierSide.pierWest,
            RightAscension = recorded.RaDegrees / 15, Declination = recorded.DecDegrees,
            Altitude = recorded.AltitudeDegrees, Azimuth = recorded.AzimuthDegrees,
            Coordinates = new Coordinates(recorded.RaDegrees, recorded.DecDegrees, Epoch.JNOW, Coordinates.RAType.Degrees),
            UTCDate = DateTime.SpecifyKind(recorded.DeviceUtc.UtcDateTime, DateTimeKind.Unspecified),
        };
        Assert.Equal(recorded, StartupGuideMountReadback.Read(info, recorded.ObservedUtc));
    }

    [Fact]
    public async Task ExposureCompletionRequiresAPostFrameOwnerPollAndChecksSafetyWhileWaiting()
    {
        var reads = 0; var checks = 0;
        var afterUtc = RecordedBefore.DeviceUtc;
        var result = await StartupGuideStationaryMount.ReadFreshAsync(() => ++reads < 3 ? RecordedBefore : RecordedAfter,
            "mount", 2, _ => { checks++; return Task.CompletedTask; }, default);
        Assert.Equal(RecordedAfter, result);
        Assert.Equal(3, reads);
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task FrozenOwnerPollTimesOutWithoutAnyCommandPort()
    {
        await Assert.ThrowsAsync<PhysicalActionGateException>(() => StartupGuideStationaryMount.ReadFreshAsync(
            () => RecordedBefore, "mount", 2, _ => Task.CompletedTask, default, TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public async Task CancelAndSafetyFailureCannotRetireOldRecords()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupGuideStationaryMount.ReadFreshAsync(
            () => RecordedBefore, "mount", 2,
            token => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cancelled.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartupGuideStationaryMount.ReadFreshAsync(
            () => RecordedBefore, "mount", 2, _ => throw new InvalidOperationException("unsafe"), default));
    }

    private static G3FieldMountBinding Binding(StartupGuideMountReadback before) => G3FieldMountBinding.Create(
        "run", "action", "preset", Path.Combine(Path.GetTempPath(), "startup-only.fit"), new string('A', 64),
        before.ObservedUtc.AddMilliseconds(-1), before.SkyReadback);
    private static GateResult Provenance(G3FieldMountBinding binding) => G3FieldMountBindingPolicy.ValidateProvenance(
        binding, "run", "action", "preset", binding.FramePath, binding.FrameSha256);
    private static GateResult Motion(G3FieldMountBinding binding, StartupGuideMountReadback after) => G3FieldMountBindingPolicy.ValidateForMotion(
        binding, "run", "action", "preset", binding.FramePath, binding.FrameSha256, after.RaDegrees, after.DecDegrees, after.Epoch, after.PierSide, 2);
}
