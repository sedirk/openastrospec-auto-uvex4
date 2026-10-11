using System.Reflection;
using System.Runtime.CompilerServices;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class QhyLiveFocusPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-04T16:45:40Z");
    private static readonly Guid JobId = Guid.Parse("8dfbec81-a7a3-4af5-8299-4fd14ee3a7ac");

    [Fact]
    public void EwLacThirtyMinuteExpiryRefreshesFromOwnedPhotometryNotWallClock()
    {
        var old = QhyLiveFocusPolicy.FromFrame(Frame(Now.AddMinutes(-37)), "qhy")!;
        Assert.True(old.ValidUntilUtc < Now);
        var frame = Frame(Now.AddSeconds(-3));
        var snapshot = Job(frame);
        var fresh = QhyLiveFocusPolicy.RefreshFromPhotometry(snapshot, JobId, "run", "qhy", old, Now)!;
        Assert.Equal(frame.ExposureEndedUtc, fresh.VerifiedUtc);
        Assert.Equal(frame.ExposureEndedUtc.AddMinutes(30), fresh.ValidUntilUtc);
        Assert.Equal(frame.Sha256, fresh.Evidence.EvidenceSha256);
        Assert.Equal(2.9, fresh.Evidence.Value);
        Assert.Same(fresh, QhyLiveFocusPolicy.RefreshFromPhotometry(snapshot, JobId, "run", "qhy", fresh, Now.AddSeconds(10)));
        Assert.Equal(Now.AddMinutes(-7), old.ValidUntilUtc); // Original evidence was not changed.
    }

    [Theory]
    [InlineData("job")]
    [InlineData("run")]
    [InlineData("camera")]
    [InlineData("acquisition")]
    [InlineData("operator")]
    public void DifferentBindingOrOperatorInterventionCannotRefresh(string mismatch)
    {
        var old = QhyLiveFocusPolicy.FromFrame(Frame(Now.AddMinutes(-37)), "qhy");
        var snapshot = Job(Frame(Now.AddSeconds(-1)));
        snapshot = mismatch switch
        {
            "job" => snapshot with { Id = Guid.NewGuid() },
            "run" => snapshot with { ObservationRunId = "other" },
            "camera" => snapshot with { ExpectedCameraStableId = "other" },
            "acquisition" => snapshot with { Kind = QhyJobKind.Acquisition },
            _ => snapshot with { OperatorInterventionRequired = true },
        };
        Assert.Same(old, QhyLiveFocusPolicy.RefreshFromPhotometry(snapshot, JobId, "run", "qhy", old, Now));
    }

    [Theory]
    [InlineData("cloud")]
    [InlineData("nan")]
    [InlineData("stars")]
    [InlineData("hash")]
    [InlineData("future")]
    [InlineData("stale")]
    [InlineData("prejob")]
    [InlineData("role")]
    public void InvalidNewImageCannotResurrectExpiredFocus(string invalid)
    {
        var old = QhyLiveFocusPolicy.FromFrame(Frame(Now.AddMinutes(-37)), "qhy");
        var frame = Frame(Now.AddSeconds(-1));
        frame = invalid switch
        {
            "cloud" => frame with { Metrics = frame.Metrics with { QualityFlags = ["LOW_TRANSPARENCY"] } },
            "nan" => frame with { Metrics = frame.Metrics with { MedianFwhmPixels = double.NaN } },
            "stars" => frame with { Metrics = frame.Metrics with { DetectedStars = 0 } },
            "hash" => frame with { Sha256 = "not-immutable" },
            "future" => Frame(Now.AddSeconds(1)),
            "stale" => Frame(Now.AddMinutes(-31)),
            "prejob" => Frame(Now.AddHours(-2)),
            _ => frame with { Role = "ACQUISITION" },
        };
        Assert.Same(old, QhyLiveFocusPolicy.RefreshFromPhotometry(Job(frame), JobId, "run", "qhy", old, Now));
    }

    [Fact]
    public void NewestEligibleImageWinsRegardlessOfSnapshotOrdering()
    {
        var newer = Frame(Now.AddSeconds(-1));
        var snapshot = Job(newer) with { Frames = [newer, Frame(Now.AddSeconds(-15))] };
        Assert.Equal(newer.Sha256, QhyLiveFocusPolicy.RefreshFromPhotometry(snapshot, JobId, "run", "qhy", null, Now)!.Evidence.EvidenceSha256);
    }

    [Theory]
    [InlineData("FOCUS_GS350_WIDE_FIELD_LIVE_METRIC")]
    [InlineData("FOCUS_GS350_WIDE_FIELD_POSITION")]
    [InlineData("FOCUS_GS350_WIDE_FIELD_IDENTITY")]
    [InlineData("QHY_TEMPERATURE")]
    public void OptionalEvidenceFailureIsWarningButNotForgedFocusSuccess(string code)
    {
        var source = GateResult.Fail(code, "fixture expired/mismatched");
        var warning = QhyLiveFocusPolicy.Degradation([source])!;
        Assert.Equal(GateDisposition.Passed, warning.Disposition);
        Assert.Equal(GateSeverity.Warning, warning.Severity);
        Assert.Contains(code, warning.Message);
        Assert.Equal(GateDisposition.Failed, source.Disposition);
    }

    [Theory]
    [InlineData("FOCUS_C11_MAIN_POSITION")]
    [InlineData("FOCUS_UVEX_SPECTRAL_POSITION")]
    [InlineData("ATR_TEMPERATURE")]
    [InlineData("PHD2_IDENTITY_MISMATCH")]
    [InlineData("GUIDING_UNSTABLE")]
    [InlineData("ENVIRONMENT_SAFETY_TRIPPED")]
    [InlineData("NIGHT_SETUP_SNAPSHOT_UNTRUSTED")]
    public void SpectroscopySafetyAndGuideFailuresRemainBlocking(string code) =>
        Assert.Null(QhyLiveFocusPolicy.Degradation([GateResult.Fail(code, "still blocks")]));

    [Fact]
    public async Task ActualRunnerIsolationKeepsJobHandleAndScienceCounters()
    {
        var runner = (RealObservationStageRunner)RuntimeHelpers.GetUninitializedObject(typeof(RealObservationStageRunner));
        Set("qhyAvailableForRun", true);
        Set("photometryJobId", (Guid?)JobId);
        var f = new AtrWorkflowFixture();
        await f.Science.SelectExposureAsync(f.Context, default);
        var captures = 0;
        f.Port.NextMetrics = m => m with { GuidingStable = ++captures == 1 };
        await f.Science.RunAsync(f.Context, default);
        Set("atrProbe", f.Probe);
        Set("atrScience", f.Science);
        Set("currentQhyFocusMetric", QhyLiveFocusPolicy.FromFrame(Frame(Now.AddMinutes(-37)), "qhy"));
        typeof(RealObservationStageRunner).GetMethod("DisableQhyForRun", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(runner, ["expired focus metric"]);
        Assert.False((bool)Get("qhyAvailableForRun")!);
        Assert.Null(Get("currentQhyFocusMetric"));
        Assert.Null(Get("lastQhySolve"));
        Assert.Equal(JobId, (Guid)Get("photometryJobId")!);
        Assert.Equal(2, f.Science.AcceptedFrames);
        Assert.Equal(3, f.Science.AttemptedFrames);
        void Set(string name, object? value) => typeof(RealObservationStageRunner).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(runner, value);
        object? Get(string name) => typeof(RealObservationStageRunner).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runner);
    }

    [Fact]
    public void SharedProductionRunnerRefreshesBeforeLiveValidationAndIsolatesBeforeHardReturn()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var refresh = source.IndexOf("await RefreshQhyLiveFocusMetricAsync(context", StringComparison.Ordinal);
        var build = source.IndexOf("focusDomainEvidence.BuildLiveFocusDomains(", refresh, StringComparison.Ordinal);
        var isolate = source.IndexOf("await DegradeOptionalQhyLiveSetupAsync(liveSetupGates", build, StringComparison.Ordinal);
        var hardReturn = source.IndexOf("if (incompatibleSetup is not null) return incompatibleSetup;", isolate, StringComparison.Ordinal);
        Assert.True(refresh >= 0 && build > refresh && isolate > build && hardReturn > isolate);
        Assert.Contains("!qhy.HasOwnerSession(jobId)", source);
        Assert.Contains("ValidateAtrScienceMetrics(captured.Metrics)", WorkflowSource.Science);
    }

    private static QhyFrameRecord Frame(DateTimeOffset ended) => new(Guid.NewGuid(), 1, "PHOTOMETRY-R", "fixture.fits", "",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ended.ToString("O")))),
        ended.AddSeconds(-5), ended.AddSeconds(-2.5), ended, new(5, 10, 0),
        new(0, 10000, 100, 90, 10, 110, 200, 500, 0, 0, 20, 2.9, 0.1, 100, 1, []));

    private static QhyJobSnapshot Job(QhyFrameRecord frame) => new(JobId, "run", QhyJobKind.Photometry,
        QhyJobState.Running, Now.AddHours(-1), Now.AddHours(-1), null, "EW Lac", "qhy", null, null, [frame], [], "manifest");
}
