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
