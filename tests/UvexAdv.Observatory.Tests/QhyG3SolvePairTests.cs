using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class QhyG3SolvePairTests
{
    [Fact]
    public void SamePointingPairBuildsHashedCandidateAcrossRaWrap()
    {
        var result = QhyG3SolvePairBuilder.Build(Request());

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        var candidate = Assert.IsType<QhyToG3TransferCandidate>(result.Candidate);
        Assert.Equal(QhyToG3TransferLifecycle.Candidate, candidate.Lifecycle);
        Assert.False(candidate.MotionAuthority);
        Assert.Equal(1, candidate.SampleCount);
        Assert.InRange(candidate.Model.G3MinusQhyEastArcseconds, 6.23, 6.24);
        Assert.InRange(candidate.Model.G3MinusQhyNorthArcseconds, 3.59, 3.61);
        Assert.Equal(-candidate.Model.G3MinusQhyEastArcseconds, candidate.Model.PredictedPrepositionEastArcseconds, 9);
        Assert.Equal(-candidate.Model.G3MinusQhyNorthArcseconds, candidate.Model.PredictedPrepositionNorthArcseconds, 9);
        Assert.Empty(candidate.ValidateIntegrity());
    }

    [Fact]
    public void MountMovementRejectsPair()
    {
        var request = Request();
        var moved = request.MountReadbacks.ToArray();
        moved[^1] = moved[^1] with { RightAscensionDegrees = 0.01 };

        var result = QhyG3SolvePairBuilder.Build(request with { MountReadbacks = moved });

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("QHY_G3_PAIR_MOUNT_MOVED", result.Gate.Code);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void DuplicateMountRoleRejectsPair()
    {
        var request = Request();
        var duplicated = request.MountReadbacks.ToArray();
        duplicated[^1] = duplicated[^1] with { Role = "qhy-after-accepted-frame" };

        var result = QhyG3SolvePairBuilder.Build(request with { MountReadbacks = duplicated });

        Assert.Equal("QHY_G3_PAIR_MOUNT_BRACKET_INCOMPLETE", result.Gate.Code);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void FinalReadbackMustFollowBothSolves()
    {
        var request = Request();
        var invalid = request.MountReadbacks.ToArray();
        invalid[^1] = invalid[^1] with { ReportedUtc = request.Qhy.ExposureEndedUtc };

        var result = QhyG3SolvePairBuilder.Build(request with { MountReadbacks = invalid });

        Assert.Equal("QHY_G3_PAIR_MOUNT_BRACKET_TIME_INVALID", result.Gate.Code);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void PairTimeWindowIsStrict()
    {
        var request = Request();
        var lateQhy = request.Qhy with
        {
            ExposureStartedUtc = request.Qhy.ExposureStartedUtc.AddSeconds(40),
            ExposureMidpointUtc = request.Qhy.ExposureMidpointUtc.AddSeconds(40),
            ExposureEndedUtc = request.Qhy.ExposureEndedUtc.AddSeconds(40),
            SolveCompletedUtc = request.Qhy.SolveCompletedUtc.AddSeconds(40),
        };
        var lateReadbacks = request.MountReadbacks.Select(readback => readback.Role is
                "qhy-before-job" or "qhy-after-accepted-frame" or "pair-final-readback"
            ? readback with { ReportedUtc = readback.ReportedUtc.AddSeconds(40) }
            : readback).ToArray();

        var result = QhyG3SolvePairBuilder.Build(request with
        {
            Qhy = lateQhy,
            MountReadbacks = lateReadbacks,
            CreatedUtc = request.CreatedUtc.AddSeconds(40),
        });

        Assert.Equal("QHY_G3_PAIR_TIME_WINDOW_EXCEEDED", result.Gate.Code);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void PairWallClockIncludesSolveAndCandidateLatency()
    {
        var request = Request();

        var result = QhyG3SolvePairBuilder.Build(request with
        {
            CreatedUtc = request.CreatedUtc.AddSeconds(30),
        });

        Assert.Equal("QHY_G3_PAIR_TIME_WINDOW_EXCEEDED", result.Gate.Code);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void DelayedFormalDeliveryTrainsHistoricalPairWithoutRefreshingMeasurementOrAuthority()
    {
        var request = DelayedRequest();
        var candidate = Assert.IsType<QhyToG3TransferCandidate>(QhyG3SolvePairBuilder.Build(request).Candidate);
        Assert.Equal(2, candidate.SchemaVersion);
        Assert.Equal(46, candidate.PairWallClockSeconds);
        Assert.Equal(5, candidate.ExposureSupportSeconds);
        Assert.Equal(request.Qhy.ExposureEndedUtc, candidate.MeasurementUtc);
        Assert.Equal(candidate.MeasurementUtc + request.Policy.CandidateValidity, candidate.ValidUntilUtc);
        Assert.False(candidate.MotionAuthority);
        Assert.Empty(candidate.ValidateIntegrity());
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<QhyToG3TransferCandidate>(
            System.Text.Json.JsonSerializer.Serialize(candidate))!;
        var rebuilt = QhyG3SolvePairBuilder.Build(request with { ExposureTiming = reloaded.ExposureTiming });
        Assert.Equal(candidate.CandidateSha256, rebuilt.Candidate?.CandidateSha256);
        Assert.Empty(reloaded.ValidateIntegrity());
    }

    [Fact]
    public void DelayedPairCannotHideUnknownShutterTimingOrChangedSource()
    {
        var request = DelayedRequest();
        Assert.Equal("QHY_G3_PAIR_SHUTTER_BRACKET_INVALID", QhyG3SolvePairBuilder.Build(request with
            { G3 = request.G3 with { TimingAuthority = "save_image completion minus exposure" } }).Gate.Code);
        Assert.Equal("QHY_G3_PAIR_SHUTTER_BRACKET_INVALID", QhyG3SolvePairBuilder.Build(request with
            { ExposureTiming = null }).Gate.Code);
        Assert.Equal("QHY_G3_PAIR_SHUTTER_BRACKET_INVALID", QhyG3SolvePairBuilder.Build(request with
            { ExposureTiming = new(1, 3) }).Gate.Code);
        var candidate = QhyG3SolvePairBuilder.Build(request).Candidate!;
        Assert.Contains((candidate with { ExposureTiming = new(1, 1) }).ValidateIntegrity(), issue => issue.Contains("self-hash"));
    }

    [Fact]
    public void DelayedPairStillRequiresFullLateStationaryAndSamePierBracket()
    {
        var request = DelayedRequest();
        var moved = request.MountReadbacks.ToArray();
        moved[^1] = moved[^1] with { RightAscensionDegrees = 0.01 };
        Assert.Equal("QHY_G3_PAIR_MOUNT_MOVED", QhyG3SolvePairBuilder.Build(request with { MountReadbacks = moved }).Gate.Code);
        moved[^1] = request.MountReadbacks[^1] with { PierSide = "pierWest" };
        Assert.Equal("QHY_G3_PAIR_MOUNT_TOPOLOGY_CHANGED", QhyG3SolvePairBuilder.Build(request with { MountReadbacks = moved }).Gate.Code);
        moved[^1] = request.MountReadbacks[^1] with { ReportedUtc = request.G3.ExposureEndedUtc };
        Assert.Equal("QHY_G3_PAIR_MOUNT_BRACKET_TIME_INVALID", QhyG3SolvePairBuilder.Build(request with { MountReadbacks = moved }).Gate.Code);
    }

    [Fact]
    public void DelayedPairLimitsExposureSupportAndWorstPossibleShutterMidpoint()
    {
        var request = DelayedRequest();
        // A 30-second native PC bracket around a 2-second shutter is NOT a
        // precise midpoint. The centre looks close; the worst shutter does not.
        var g3 = request.G3 with { ExposureEndedUtc = request.G3.ExposureStartedUtc.AddSeconds(30),
            ExposureMidpointUtc = request.G3.ExposureStartedUtc.AddSeconds(15) };
        var readbacks = request.MountReadbacks.ToArray();
        readbacks[1] = readbacks[1] with { ReportedUtc = g3.ExposureEndedUtc };
        Assert.Equal("QHY_G3_PAIR_TIME_WINDOW_EXCEEDED", QhyG3SolvePairBuilder.Build(request with
            { G3 = g3, MountReadbacks = readbacks }).Gate.Code);
        // Even a precise shutter cannot make a >30-second capture pair local.
        g3 = g3 with { ExposureEndedUtc = g3.ExposureStartedUtc.AddSeconds(31),
            ExposureMidpointUtc = g3.ExposureStartedUtc.AddSeconds(15.5) };
        readbacks[1] = readbacks[1] with { ReportedUtc = g3.ExposureEndedUtc };
        Assert.Equal("QHY_G3_PAIR_TIME_WINDOW_EXCEEDED", QhyG3SolvePairBuilder.Build(request with
            { G3 = g3, MountReadbacks = readbacks, ExposureTiming = new(1, 31) }).Gate.Code);
    }

    [Fact]
    public void EveryNewNativePairIncludesUncertainShutterTimingIncludingImmediateSource()
    {
        var request = DelayedRequest() with { PairSource = QhyG3SolvePairSource.ImmediateSingleQhyExposure };
        var candidate = Assert.IsType<QhyToG3TransferCandidate>(QhyG3SolvePairBuilder.Build(request).Candidate);
        Assert.Equal(2, candidate.SchemaVersion);
        Assert.NotNull(candidate.ExposureTiming);
        var legacy = QhyG3SolvePairBuilder.Build(Request()).Candidate!;
        Assert.Equal(1, legacy.SchemaVersion);
        Assert.Null(legacy.ExposureTiming);
        Assert.Equal(legacy.CreatedUtc, legacy.MeasurementUtc);
    }

    [Fact]
    public void SolverArrivalDoesNotRefreshLocalTrainingAgeOrPermitFutureDelivery()
    {
        var candidate = QhyG3SolvePairBuilder.Build(DelayedRequest()).Candidate!;
        var sample = new QhyG3VerifiedLocalSample(candidate, new("focus", 5000, Hash('A'), null, "ExplicitManualLock", 50), "focus.json", Hash('B'));
        var now = candidate.MeasurementUtc.AddMinutes(30).AddSeconds(1);
        var current = candidate.Qhy with { ExposureStartedUtc = now.AddSeconds(-2), ExposureMidpointUtc = now.AddSeconds(-1),
            ExposureEndedUtc = now, SolveCompletedUtc = now };
        Assert.Empty(QhyG3LocalModelPolicy.SelectLocalSamples([sample], current, now, TimeSpan.FromMinutes(30)));
        now = candidate.CreatedUtc.AddSeconds(1);
        current = current with { ExposureStartedUtc = now, ExposureMidpointUtc = now, ExposureEndedUtc = now, SolveCompletedUtc = now };
        Assert.Single(QhyG3LocalModelPolicy.SelectLocalSamples([sample], current, now, TimeSpan.FromMinutes(30)));
        Assert.Empty(QhyG3LocalModelPolicy.SelectLocalSamples([sample], current, candidate.CreatedUtc.AddSeconds(-1), TimeSpan.FromMinutes(30)));
    }

    private static QhyG3SolvePairBuildRequest DelayedRequest()
    {
        var request = Request();
        var readbacks = request.MountReadbacks.ToArray();
        readbacks[^1] = readbacks[^1] with { ReportedUtc = request.CreatedUtc.AddSeconds(40) };
        return request with
        {
            PairSource = QhyG3SolvePairSource.ExposureTimedHistoricalFormalPair,
            ExposureTiming = new(1, 2),
            G3 = request.G3 with { TimingAuthority = QhyG3ExposureTiming.NativeBracketAuthority,
                SolveCompletedUtc = request.CreatedUtc.AddSeconds(39) },
            CreatedUtc = request.CreatedUtc.AddSeconds(40), MountReadbacks = readbacks,
        };
    }

    [Fact]
    public void CandidateHashDetectsAnyModelEdit()
    {
        var candidate = Assert.IsType<QhyToG3TransferCandidate>(QhyG3SolvePairBuilder.Build(Request()).Candidate);

        var tampered = candidate with
        {
            Model = candidate.Model with
            {
                G3MinusQhyEastArcseconds = candidate.Model.G3MinusQhyEastArcseconds + 1,
            },
        };

        Assert.Contains(tampered.ValidateIntegrity(), issue => issue.Contains("self-hash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DisabledPolicyCannotCreateCandidate()
    {
        var result = QhyG3SolvePairBuilder.Build(Request() with { Policy = QhyG3FastPairPolicy.Disabled });

        Assert.Equal("QHY_G3_PAIR_DISABLED", result.Gate.Code);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void MissingDetectorGeometryCannotCreateCandidate()
    {
        var request = Request();
        var result = QhyG3SolvePairBuilder.Build(request with
        {
            Qhy = request.Qhy with { FrameWidthPixels = 0, RoiWidthPixels = 0 },
        });

        Assert.Equal("QHY_G3_PAIR_QHY_INVALID", result.Gate.Code);
        Assert.Contains("ROI/binning", result.Gate.Message, StringComparison.Ordinal);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void ExtremeProfileTimeValuesBecomeInvalidWithoutOverflowing()
    {
        Assert.Equal(TimeSpan.Zero, QhyG3FastPairPolicy.ValidationTimeSpanFromSeconds(double.MaxValue));
        Assert.Equal(TimeSpan.Zero, QhyG3FastPairPolicy.ValidationTimeSpanFromHours(double.MaxValue));
        Assert.Equal(TimeSpan.Zero, QhyG3FastPairPolicy.ValidationTimeSpanFromSeconds(double.NaN));
    }

    private static QhyG3SolvePairBuildRequest Request()
    {
        var t0 = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
        var readbacks = new[]
        {
            new QhyG3PairMountReadback("g3-before-exposure", 359.5, 30, "JNOW", "pierEast", t0),
            new QhyG3PairMountReadback("g3-after-exposure", 359.5, 30, "JNOW", "pierEast", t0.AddSeconds(2)),
            new QhyG3PairMountReadback("qhy-before-job", 359.5, 30, "JNOW", "pierEast", t0.AddSeconds(3)),
            new QhyG3PairMountReadback("qhy-after-accepted-frame", 359.5, 30, "JNOW", "pierEast", t0.AddSeconds(5)),
            new QhyG3PairMountReadback("pair-final-readback", 359.5, 30, "JNOW", "pierEast", t0.AddSeconds(6)),
        };
        return new QhyG3SolvePairBuildRequest(
            new QhyG3FastPairPolicy(
                1,
                "fast-pair-v1",
                true,
                2,
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(30),
                2,
                TimeSpan.FromDays(1),
                10),
            "run-1",
            Hash('A'),
            Hash('B'),
            "night-1",
            Hash('C'),
            "install-1",
            "mount-1",
            "GS350/QHY",
            "C11/G3",
            QhyG3SolvePairSource.ImmediateSingleQhyExposure,
            new QhyG3SolvedFrame(
                "QHY/GS350",
                "qhy-1",
                "qhy.fit",
                Hash('D'),
                "qhy-solve.json",
                Hash('E'),
                t0.AddSeconds(3),
                t0.AddSeconds(4),
                t0.AddSeconds(5),
                t0.AddSeconds(5.5),
                "QHY manifest midpoint",
                3056,
                2048,
                1,
                1,
                0,
                0,
                3056,
                2048,
                359.999,
                30,
                2.1,
                5,
                false,
                Hash('F')),
            new QhyG3SolvedFrame(
                "PHD2/G3/C11",
                "g3-1",
                "g3.fit",
                Hash('1'),
                "g3-solve.json",
                Hash('2'),
                t0,
                t0.AddSeconds(1),
                t0.AddSeconds(2),
                t0.AddSeconds(2.5),
                "PHD2 completion minus exposure",
                1920,
                1080,
                1,
                1,
                0,
                0,
                1920,
                1080,
                0.001,
                30.001,
                0.38,
                -10,
                false,
                Hash('3')),
            readbacks,
            t0.AddSeconds(6));
    }

    private static string Hash(char value) => new(value, 64);
}
