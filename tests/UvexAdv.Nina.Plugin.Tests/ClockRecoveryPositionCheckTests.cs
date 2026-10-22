using System.Collections.Concurrent;
using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ClockRecoveryPositionCheckTests
{
    private static readonly DateTimeOffset Issued = new(2026, 10, 21, 16, 9, 55, TimeSpan.Zero);
    private static readonly DateTimeOffset Deadline = Issued.AddSeconds(173);
    private static readonly ClockRecoveryPositionIdentity Identity = new(
        new EquatorialTarget("HD 218393", "HD 218393", 346.2, 43.1), new string('A', 64),
        new string('B', 64), "test-mount", Guid.Parse("89b9b982-5d07-44d6-b0e5-09c31a459f61"),
        "localhost:4400", 1, "test-profile", "test-stable-g3");

    private static ClockRecoveryPositionSnapshot Snapshot(DateTimeOffset at) => new(Identity,
        new G3FrameMountReadback(346.2, 43.1, "JNOW", "pierEast", at), true, true, true, true);

    private static ClockRecoveryPositionCheck Create() => Assert.IsType<ClockRecoveryPositionCheck>(
        ClockRecoveryPositionCheck.TryCreate("original", GateResult.Pass("MOUNT_CLOCK_RECOVERED_BY_NINA_RECONNECT", "verified"),
            Snapshot(Issued.AddSeconds(-30)), Snapshot(Issued), Issued, Deadline, 2, out _));

    [Fact]
    public void SuccessfulRepairIssuesOnlyOneFreshSameTargetInspectionPermission()
    {
        var check = Create();
        Assert.Equal("original", check.SourceRunId);
        Assert.Equal(Issued, check.IssuedUtc);
        Assert.Equal(Deadline, check.OriginalRecoveryDeadlineUtc);
        Assert.False(check.Attempted);
        Assert.True(check.TryConsume("continuation", Snapshot(Issued.AddSeconds(1)), Issued.AddSeconds(1), out _));
        Assert.True(check.Attempted);
        Assert.Equal("continuation", check.AttemptedRunId);
        Assert.Equal("continuation", check.ConsumedRunId);
        Assert.False(check.TryConsume("another-segment", Snapshot(Issued.AddSeconds(2)), Issued.AddSeconds(2), out _));
        Assert.Equal("continuation", check.ConsumedRunId);
    }

    [Theory]
    [InlineData("failed-clock")]
    [InlineData("unknown-clock")]
    [InlineData("missing-run")]
    [InlineData("unjoined-before")]
    [InlineData("unjoined-after")]
    [InlineData("side-changed")]
    [InlineData("unknown-side")]
    [InlineData("epoch-changed")]
    [InlineData("moving-after")]
    [InlineData("untracked-before")]
    [InlineData("parked-after")]
    [InlineData("position-shift")]
    [InlineData("reversed-readbacks")]
    [InlineData("future-readback")]
    [InlineData("expired-budget")]
    [InlineData("invalid-limit")]
    public void CreationDeclinesIneligibleContinuityWithoutProducingAnObservationFailure(string scenario)
    {
        var source = "original";
        var gate = GateResult.Pass("MOUNT_CLOCK_RECOVERED_BY_NINA_RECONNECT", "verified");
        var before = Snapshot(Issued.AddSeconds(-30));
        var after = Snapshot(Issued);
        var deadline = Deadline;
        var limit = 2d;
        switch (scenario)
        {
            case "failed-clock": gate = GateResult.Fail("MOUNT_CLOCK_OFFSET_EXCEEDED", "not repaired"); break;
            case "unknown-clock": gate = GateResult.Unknown("MOUNT_CLOCK_UNAVAILABLE", "unknown"); break;
            case "missing-run": source = ""; break;
            case "unjoined-before": before = before with { OwnersStoppedAndJoined = false }; break;
            case "unjoined-after": after = after with { OwnersStoppedAndJoined = false }; break;
            case "side-changed": after = after with { Mount = after.Mount with { PierSide = "pierWest" } }; break;
            case "unknown-side": after = after with { Mount = after.Mount with { PierSide = "pierUnknown" } }; break;
            case "epoch-changed": after = after with { Mount = after.Mount with { CoordinateEpoch = "J2000" } }; break;
            case "moving-after": after = after with { Stationary = false }; break;
            case "untracked-before": before = before with { Tracking = false }; break;
            case "parked-after": after = after with { Parked = true }; break;
            case "position-shift": after = after with { Mount = after.Mount with { DeclinationDegrees = 43.101 } }; break;
            case "reversed-readbacks": before = before with { Mount = before.Mount with { ReportedUtc = Issued.AddSeconds(1) } }; break;
            case "future-readback": after = after with { Mount = after.Mount with { ReportedUtc = Issued.AddSeconds(1) } }; break;
            case "expired-budget": deadline = Issued; break;
            case "invalid-limit": limit = double.NaN; break;
        }
        Assert.Null(ClockRecoveryPositionCheck.TryCreate(source, gate, before, after, Issued, deadline, limit, out var reason));
        Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("coordinates")]
    [InlineData("action-config")]
    [InlineData("commissioning")]
    [InlineData("mount")]
    [InlineData("nina-profile")]
    [InlineData("phd-endpoint")]
    [InlineData("phd-profile")]
    [InlineData("phd-profile-name")]
    [InlineData("g3")]
    public void IdentityChangeAtRepairOrConsumptionCannotReusePermission(string scenario)
    {
        var current = Snapshot(Issued.AddSeconds(1));
        var changed = scenario switch
        {
            "target" => Identity with { Target = Identity.Target with { Name = "HD 224559", CatalogId = "HD 224559" } },
            "coordinates" => Identity with { Target = Identity.Target with { DeclinationDegrees = 43.2 } },
            "action-config" => Identity with { SharedActionConfigurationSha256 = new string('C', 64) },
            "commissioning" => Identity with { CommissioningPresetSha256 = new string('D', 64) },
            "mount" => Identity with { TelescopeId = "other-mount" },
            "nina-profile" => Identity with { NinaProfileId = Guid.NewGuid() },
            "phd-endpoint" => Identity with { Phd2Endpoint = "localhost:4401" },
            "phd-profile" => Identity with { Phd2ProfileId = 2 },
            "phd-profile-name" => Identity with { Phd2ProfileName = "other-profile" },
            "g3" => Identity with { G3StableCameraId = "other-camera" },
            _ => throw new InvalidOperationException(scenario),
        };
        Assert.Null(ClockRecoveryPositionCheck.TryCreate("original", GateResult.Pass("clock", "verified"),
            Snapshot(Issued.AddSeconds(-1)), Snapshot(Issued) with { Identity = changed }, Issued, Deadline, 2, out _));
        var check = Create();
        Assert.False(check.TryConsume("continuation", current with { Identity = changed }, current.Mount.ReportedUtc, out _));
        Assert.Null(check.ConsumedRunId);
        // A failed attempt is not permission to retry the shortcut after restoring settings.
        Assert.False(check.TryConsume("continuation", current, current.Mount.ReportedUtc, out _));
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("moving")]
    [InlineData("not-tracking")]
    [InlineData("not-joined")]
    [InlineData("parked")]
    [InlineData("side")]
    [InlineData("unknown-side")]
    [InlineData("epoch")]
    [InlineData("drift")]
    [InlineData("nan-position")]
    [InlineData("old-readback")]
    [InlineData("future-readback")]
    public void ConsumptionUsesFreshStationaryOwnerProofRatherThanRepairSnapshotAlone(string scenario)
    {
        var now = Issued.AddSeconds(1);
        var current = Snapshot(now);
        current = scenario switch
        {
            "disconnected" => current with { Connected = false },
            "moving" => current with { Stationary = false },
            "not-tracking" => current with { Tracking = false },
            "not-joined" => current with { OwnersStoppedAndJoined = false },
            "parked" => current with { Parked = true },
            "side" => current with { Mount = current.Mount with { PierSide = "pierWest" } },
            "unknown-side" => current with { Mount = current.Mount with { PierSide = "normal" } },
            "epoch" => current with { Mount = current.Mount with { CoordinateEpoch = "J2000" } },
            "drift" => current with { Mount = current.Mount with { DeclinationDegrees = 43.101 } },
            "nan-position" => current with { Mount = current.Mount with { RightAscensionDegrees = double.NaN } },
            "old-readback" => current with { Mount = current.Mount with { ReportedUtc = Issued.AddSeconds(-1) } },
            "future-readback" => current with { Mount = current.Mount with { ReportedUtc = now.AddSeconds(1) } },
            _ => throw new InvalidOperationException(scenario),
        };
        var check = Create();
        Assert.False(check.TryConsume("continuation", current, now, out var reason));
        Assert.NotEmpty(reason);
        Assert.True(check.Attempted);
        Assert.Null(check.ConsumedRunId);
    }

    [Fact]
    public void OriginalDeadlineIsNotReplacedWithFreshGracePeriod()
    {
        var check = Create();
        // Still valid after an arbitrary 120-second age, but not at the old deadline.
        Assert.True(check.TryConsume("continuation", Snapshot(Issued.AddSeconds(150)), Issued.AddSeconds(150), out _));
        Assert.False(Create().TryConsume("continuation", Snapshot(Deadline), Deadline, out _));
        Assert.False(Create().TryConsume("continuation", Snapshot(Issued.AddSeconds(-1)), Issued.AddSeconds(-1), out _));
        Assert.False(Create().TryConsume("original", Snapshot(Issued), Issued, out _));
    }

    [Fact]
    public void RecordedSmallReconnectReadbackChangeDoesNotRequireAnotherCatalogueGoto()
    {
        var before = Snapshot(Issued.AddSeconds(-30));
        var after = Snapshot(Issued) with { Mount = Snapshot(Issued).Mount with { DeclinationDegrees = 43.1 + 1.964 / 3600 } };
        var check = Assert.IsType<ClockRecoveryPositionCheck>(ClockRecoveryPositionCheck.TryCreate("original",
            GateResult.Pass("clock", "verified"), before, after, Issued, Deadline, 2, out _));
        Assert.True(check.TryConsume("continuation", after with { Mount = after.Mount with { ReportedUtc = Issued.AddSeconds(1) } },
            Issued.AddSeconds(1), out _));
    }

    [Fact]
    public void RightAscensionWrapIsComparedOnTheSphere()
    {
        var before = Snapshot(Issued.AddSeconds(-1)) with { Mount = Snapshot(Issued).Mount with { RightAscensionDegrees = 359.9999, ReportedUtc = Issued.AddSeconds(-1) } };
        var after = Snapshot(Issued) with { Mount = Snapshot(Issued).Mount with { RightAscensionDegrees = 0.0001 } };
        Assert.NotNull(ClockRecoveryPositionCheck.TryCreate("original", GateResult.Pass("clock", "verified"),
            before, after, Issued, Deadline, 2, out _));
    }

    [Fact]
    public void RacingConsumersCannotDuplicateTheCurrentFieldEpisode()
    {
        var check = Create();
        var accepted = new ConcurrentBag<string>();
        Parallel.For(0, 16, i =>
        {
            var run = $"continuation-{i}";
            if (check.TryConsume(run, Snapshot(Issued.AddSeconds(1)), Issued.AddSeconds(1), out _)) accepted.Add(run);
        });
        Assert.Equal(Assert.Single(accepted), check.ConsumedRunId);
    }
}
