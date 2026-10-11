using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2ConvergenceWorkflowTests
{
    [Theory]
    [InlineData(1)] [InlineData(3)]
    public async Task OneGoodLastFrameDoesNotReplaceTheCompleteWindow(int initialCount)
    {
        var f = new Phd2ConvergenceFixture();
        var request = f.Request(initialCount == 1 ? [.5] : [2, 2, .5]);
        f.Trace.Handlers["CapturePhd2PlacementGuideWindowAsync"] = a =>
        {
            Assert.Equal(3, a[7]);
            Assert.Equal(request.Ledger.StartedUtc.AddSeconds(request.Preset.MaximumElapsedSeconds), a[8]);
            return Task.FromResult<IReadOnlyList<Phd2GuidingResidualState>>(f.Window([.4, .3, .2], 'D'));
        };
        var result = await f.Workflow.ConvergeAsync(f.Context, request, default);
        Assert.True(result.CanAdvance, result.Gate.Message);
        Assert.Single(f.Trace.Calls, c => c == "CapturePhd2PlacementGuideWindowAsync");
        Assert.DoesNotContain("SetExactLockPositionAsync", f.Trace.Calls);
        Assert.Equal(request.Ledger.AttemptsUsed, f.Persisted[^1].AttemptsUsed);
        Assert.Equal(f.Window([.4, .3, .2], 'D')[^1].Measurement.FrameSha256, f.Persisted[^1].LastAcceptedFrameSha256);
    }

    [Fact]
    public async Task ExactCompletionDoesNotCaptureAgainAndKeepsOriginalDebtCounters()
    {
        var f = new Phd2ConvergenceFixture();
        var request = f.Request([.4, .3, .2]);
        var result = await f.Workflow.ConvergeAsync(f.Context, request, default);
        Assert.True(result.CanAdvance, result.Gate.Message);
        Assert.DoesNotContain("CapturePhd2PlacementGuideWindowAsync", f.Trace.Calls);
        Assert.DoesNotContain("SetExactLockPositionAsync", f.Trace.Calls);
        Assert.Equal(request.Ledger.StartedUtc, Assert.Single(f.Persisted).StartedUtc);
        Assert.Equal(request.Ledger.CumulativeCommandedPixels, f.Persisted[0].CumulativeCommandedPixels);
    }

    [Fact]
    public async Task WarningAfterMeasuredWindowReallyHandsOffToAtrWithoutClaimingPrecisePlacement()
    {
        var f = new Phd2ConvergenceFixture(warning: true);
        var result = await f.Workflow.ConvergeAsync(f.Context, f.Request([4.504, 3.328, 5.140]), default);
        Assert.Equal("PHD2_SLIT_PRECISION_WARNING_PROBE_AUTHORIZED", result.Gate.Code);
        Assert.True(result.CanAdvance);
        Assert.Equal(0, result.Gate.Metrics!["preciseSlitPlacementProven"]);
        Assert.DoesNotContain("SetExactLockPositionAsync", f.Trace.Calls);
        // Execute the next real workflow, not a second policy-only decision.
        var atr = new AtrWorkflowFixture(1);
        Assert.True((await atr.Science.SelectExposureAsync(atr.Context, default)).CanAdvance);
        Assert.True((await atr.Science.RunAsync(atr.Context, default)).CanAdvance);
        Assert.Equal(1, atr.Science.AcceptedFrames);
    }

    [Theory]
    [InlineData("weather")] [InlineData("identity")]
    [InlineData("topology")] [InlineData("budget")]
    public async Task QualityWarningNeverBypassesSafetyIdentityTopologyOrMotionBudget(string failure)
    {
        var f = new Phd2ConvergenceFixture(warning: true);
        var request = f.Request([12, 12, 12]);
        if (failure == "weather") f.Safe = false;
        if (failure == "identity") request = request with { Session = request.Session with
            { LastMeasurement = request.Session.LastMeasurement with { Measurement = request.Session.LastMeasurement.Measurement with { TargetIdentityConfirmed = false } } } };
        if (failure == "topology") request = request with { Session = request.Session with
            { Qualification = request.Session.Qualification with { CurrentTopologyFingerprintSha256 = new('0', 64) } } };
        if (failure == "budget") request = request with { Ledger = request.Ledger with { AttemptsUsed = request.Preset.MaximumAttempts + 1 } };
        var result = await f.Workflow.ConvergeAsync(f.Context, request, default);
        Assert.False(result.CanAdvance, result.Gate.Message);
        Assert.DoesNotContain("SetExactLockPositionAsync", f.Trace.Calls);
        Assert.Empty(f.Persisted);
    }

    [Fact]
    public async Task ConsumedMotionBudgetDoesNotForbidAnAlreadyMeasuredNoMotionProbe()
    {
        var f = new Phd2ConvergenceFixture(warning: true);
        var request = f.Request([4, 4, 4]);
        request = request with { Ledger = request.Ledger with { AttemptsUsed = request.Preset.MaximumAttempts } };
        var result = await f.Workflow.ConvergeAsync(f.Context, request, default);
        Assert.Equal("PHD2_SLIT_PRECISION_WARNING_PROBE_AUTHORIZED", result.Gate.Code);
        Assert.DoesNotContain("SetExactLockPositionAsync", f.Trace.Calls);
        Assert.Equal(request.Preset.MaximumAttempts, Assert.Single(f.Persisted).AttemptsUsed);
    }

    [Fact]
    public async Task CancellationAfterPrechargeKeepsReturnDebtAndNeverDispatchesAutomaticReturn()
    {
        var f = new Phd2ConvergenceFixture();
        using var cancel = new CancellationTokenSource();
        var request = f.Request([12, 12, 12]);
        f.Trace.Handlers["SetExactLockPositionAsync"] = _ =>
        {
            var intent = Assert.Single(f.Persisted);
            Assert.Equal(request.Ledger.AttemptsUsed + 1, intent.AttemptsUsed);
            Assert.True(intent.CumulativeCommandedPixels > request.Ledger.CumulativeCommandedPixels);
            Assert.Equal(request.Session.LastMeasurement.Measurement.FrameSha256, intent.LastAcceptedFrameSha256);
            cancel.Cancel(); throw new OperationCanceledException(cancel.Token);
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Workflow.ConvergeAsync(f.Context, request, cancel.Token));
        Assert.Equal(Phd2LockShiftPendingPhase.ReturnRequired, f.Persisted[^1].Phase);
        Assert.Equal(f.Persisted[0].AttemptsUsed, f.Persisted[^1].AttemptsUsed);
        Assert.DoesNotContain("ReturnPhd2LockToOriginAsync", f.Trace.Calls);
    }

    [Fact]
    public async Task OriginalDeadlineExhaustionIsTargetScopedWithoutStartingAnotherWindow()
    {
        var f = new Phd2ConvergenceFixture();
        var request = f.Request([2, 2, .5]);
        request = request with { Ledger = request.Ledger with { StartedUtc = f.Now.AddHours(-1) } };
        var result = await f.Workflow.ConvergeAsync(f.Context, request, default);
        Assert.Equal(GateFailureScope.TargetQuality, result.Gate.FailureScope);
        Assert.True(NativeSequencePlan.IsSkippableQualityFailure(result.Gate));
        Assert.DoesNotContain("CapturePhd2PlacementGuideWindowAsync", f.Trace.Calls);
        Assert.Empty(f.Persisted);
    }
}

internal sealed class Phd2ConvergenceFixture
{
    internal readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T06:00:00Z");
    internal readonly ObservationContext Context = AtrWorkflowFixture.CreateContext();
    internal readonly StrictWorkflowPort Trace;
    internal readonly Phd2SlitConvergenceWorkflow Workflow;
    internal readonly List<Phd2LockShiftPendingState> Persisted = [];
    internal bool Safe = true;
    private readonly Phd2SensorTopology topology = new("fixture", 1, "g3", "g3", "camera", "mount", new('E', 64),
        1920, 1080, 1, new(0, 0, 1920, 1080), Phd2ImageCoordinateDomain.FullSensorCoordinates,
        12.5, Phd2SensorRotationAuthority.QualifiedPhd2Calibration, "pierEast");
    private readonly SlitGeometry slit = new("fixture", new(500, 400), 0, 410, 3, .5, "g3", 1, 1);
    internal Phd2ConvergenceFixture(bool warning = false)
    {
        var port = StrictWorkflowPort.Create<IPhd2SlitPlacementPort>(out Trace);
        Trace.Handlers["ThrowIfGuideOutputFailed"] = _ => null;
        Trace.Handlers["BuildPhd2LockShiftSafetySnapshot"] = _ => new Phd2LockShiftSafetySnapshot(Safe, 60, 55, 40, topology.PierSide, Now);
        Trace.Handlers["get_PendingMotion"] = _ => null;
        Trace.Handlers["get_GuidePrecisionWarningActive"] = _ => false;
        Trace.Handlers["RecordSession"] = _ => null;
        Trace.Handlers["RecordPendingMotion"] = _ => null;
        Trace.Handlers["PromoteMeasurement"] = _ => null;
        Trace.Handlers["Report"] = _ => null;
        Trace.Handlers["ValidateCurrentFieldBindingAsync"] = _ => Task.FromResult(GateResult.Pass("bound", "fixture"));
        Trace.Handlers["RequireImmediatePhysicalActionGatesAsync"] = _ => Task.CompletedTask;
        Trace.Handlers["PublishRunJsonEvidenceAsync"] = _ => Task.FromResult("evidence.json");
        Trace.Handlers["Phd2EffectiveQualityMetrics"] = _ => new Dictionary<string, double>();
        Trace.Handlers["HandleDeniedPhd2AcquisitionBudgetAsync"] = a => Task.FromResult(new StageResult(
            GateResult.Unknown(((Phd2LockShiftAcquisitionBudget)a[3]!).Code, "denied")));
        Trace.Handlers["CreatePhd2PendingState"] = a =>
        {
            var ledger = (Phd2LockShiftLedger)a[3]!;
            return Phd2SlitPlacementCommissioningTests.CreateState("run", (Phd2LockShiftPendingPhase)a[5]!) with
            {
                StartedUtc = ledger.StartedUtc, AttemptsUsed = ledger.AttemptsUsed,
                CumulativeCommandedPixels = ledger.CumulativeCommandedPixels,
                LastAcceptedFrameSha256 = ledger.LastAcceptedFrameSha256,
            };
        };
        Trace.Handlers["PersistPendingAsync"] = a => { Persisted.Add((Phd2LockShiftPendingState)a[1]!); return Task.CompletedTask; };
        Workflow = new(new(warning, false), port, new FixedClock(Now));
    }
    internal Phd2SlitConvergenceRequest Request(double[] residuals)
    {
        var window = Window(residuals);
        var quality = new Phd2CalibrationQualityAssessment("fixture", "policy", Phd2CalibrationQualityGrade.Excellent,
            100, 1, true, true, true, true, false, 1, 1, 3, [], [], Now);
        var qualification = new Phd2LockShiftQualification(true, "qualified", [], topology.ComputeFingerprintSha256(),
            false, "policy", quality.Grade, false, true, 1, 1, 3, Now);
        var calibration = new Phd2CalibrationValidation(new(1, "g3"), new(true, 0, 10, "+", 90, 10, "-", 0),
            Now, TimeSpan.Zero, 0, [], []);
        var session = new Phd2SlitPlacementSession(Phd2SlitGuideMode.OffSlitGuideStar, topology, qualification,
            quality, calibration, new(100, 100), new(100, 100), window[0].Measurement.TargetCentroid, slit,
            window[^1], new(true, null, 10, 0, Now), 1, 1, false);
        return new(session, new("lineage", new(100, 100), new(100, 100), 2, 15, Now.AddSeconds(-10), null),
            window, Phd2SlitPlacementCommissioningTests.CreatePreset(), null!, "pending.json", new Dictionary<string, string>(), 0, 0);
    }
    internal Phd2GuidingResidualState[] Window(double[] residuals, char hashStart = 'A') => residuals.Select((r, i) =>
    {
        var hash = new string((char)(hashStart + i), 64);
        var measured = new Phd2SlitFieldMeasurement(hash, Now, topology.ComputeFingerprintSha256(), new(100, 100),
            new(500 + r, 400), new(500, 400), 100, true, 1000, true, "identity", "flux", 100, "residual",
            GuidePositionMeasuredInFrame: true);
        return new Phd2GuidingResidualState(GateResult.Pass("measured", "fixture"),
            new(hash + ".fits", hash, i, i, Now, Now, false, false, false, false),
            null!, null!, [], slit, measured, null!);
    }).ToArray();
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
