using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2ConvergenceWorkflowTests
{
    [Fact]
    public async Task Hd698BudgetDeniedStillWaitsAtExistingLockThenUsesFreshWarningWindow()
    {
        var f = new Phd2ConvergenceFixture(warning: true);
        var request = f.Request([15.3071346083, 12.7328092491, 16.7680987155]);
        var initial = request.InitialWindow.Select(s => s with
            { RuntimeSlitLocal = s.RuntimeSlitLocal with { AngleDegrees = 90 } }).ToArray();
        request = request with { InitialWindow = initial,
            Session = request.Session with { LastMeasurement = initial[^1] } };
        request = request with { Ledger = request.Ledger with
            { CumulativeCommandedPixels = 98, AttemptsUsed = 2 } };
        f.Trace.Handlers["CapturePhd2PlacementGuideWindowAsync"] = a =>
        {
            Assert.Equal(3, a[7]);
            Assert.Equal(request.Ledger.StartedUtc.AddSeconds(request.Preset.MaximumElapsedSeconds), a[8]);
            return Task.FromResult<IReadOnlyList<Phd2GuidingResidualState>>(f.Window([3.4, 3.3, 3.2], 'D'));
        };
        var result = await f.Workflow.ConvergeAsync(f.Context, request, default);
        Assert.Equal("PHD2_SLIT_PRECISION_WARNING_PROBE_AUTHORIZED", result.Gate.Code);
        Assert.Single(f.Trace.Calls, c => c == "CapturePhd2PlacementGuideWindowAsync");
        Assert.DoesNotContain("SetExactLockPositionAsync", f.Trace.Calls);
        Assert.DoesNotContain("ReturnPhd2LockToOriginAsync", f.Trace.Calls);
        Assert.Equal(98, Assert.Single(f.Persisted).CumulativeCommandedPixels);
        Assert.Equal(request.Ledger.StartedUtc, f.Persisted[0].StartedUtc);
    }

    [Fact]
    public async Task PreExposureContinuationCannotRebuildCoarseAcquisitionOnDeniedMotion()
    {
        var f = new Phd2ConvergenceFixture();
        var request = f.Request([23, 23, 24]);
        request = request with { AllowCoarseReacquisition = false,
            Ledger = request.Ledger with { AttemptsUsed = request.Preset.MaximumAttempts + 1 } };
        var result = await f.Workflow.ConvergeAsync(f.Context, request, default);
        // Overdrawn is an invalid ledger, not ordinary exhaustion at the ceiling.
        Assert.Equal("SLIT_LOCK_LEDGER_INVALID", result.Gate.Code);
        Assert.Equal(GateFailureScope.Run, result.Gate.FailureScope);
        Assert.DoesNotContain("HandleDeniedPhd2AcquisitionBudgetAsync", f.Trace.Calls);
        Assert.DoesNotContain("SetExactLockPositionAsync", f.Trace.Calls);
    }

    [Theory]
    [InlineData("attempt", "skip")] [InlineData("attempt", "cleanup-failure")] [InlineData("attempt", "end-policy")]
    [InlineData("weather", "unknown-gate")] [InlineData("topology", "unknown-gate")]
    [InlineData("invalid-ledger", "unknown-gate")] [InlineData("future-clock", "unknown-gate")]
    public async Task RealConvergenceDenialKeepsScopeAndExecutesNextNativeTargetOnlyForValidExhaustion(string failure, string boundary)
    {
        var f = new Phd2ConvergenceFixture();
        var request = f.Request([23, 23, 24]) with { AllowCoarseReacquisition = false };
        if (failure == "attempt") request = request with { Ledger = request.Ledger with { AttemptsUsed = request.Preset.MaximumAttempts } };
        if (failure == "weather") f.Safe = false;
        if (failure == "topology") request = request with { Session = request.Session with { Qualification = request.Session.Qualification with { CurrentTopologyFingerprintSha256 = new('0', 64) } } };
        if (failure == "invalid-ledger") request = request with { Ledger = request.Ledger with { CumulativeCommandedPixels = double.NaN } };
        if (failure == "future-clock") request = request with { Ledger = request.Ledger with { StartedUtc = DateTimeOffset.UtcNow.AddDays(1) } };
        var result = await f.Workflow.ConvergeAsync(f.Context, request, default);
        Assert.False(result.CanAdvance);
        Assert.Equal(failure == "attempt" ? GateFailureScope.TargetQuality : GateFailureScope.Run, result.Gate.FailureScope);
        Assert.DoesNotContain("SetExactLockPositionAsync", f.Trace.Calls);
        Assert.Empty(f.Persisted);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, result.Gate.Code, result.Gate, ObservationStage.RunScienceBlock);
    }

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

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task V460ShortenedApproachRequiresFreshMeasuredWindowBeforeWarningProbe(bool allowCoarseReacquisition)
    {
        var f = new Phd2ConvergenceFixture(warning: true);
        var request = f.V460Request() with { AllowCoarseReacquisition = allowCoarseReacquisition };
        Phd2Point? dispatched = null;
        var snapshot = Phd2StateSnapshot.Disconnected with
        { IsConnected = true, AppState = Phd2AppState.Guiding, ConnectionEpoch = 1, GuideEpoch = 1 };
        f.Trace.Handlers["SetExactLockPositionAsync"] = a =>
        {
            var move = (Phd2ExactLockPositionRequest)a[0]!;
            Assert.InRange(move.MaximumStepPixels, 22.12, 22.13);
            Assert.Equal(2, Assert.Single(f.Persisted).AttemptsUsed);
            dispatched = move.DesiredPosition;
            snapshot = snapshot with { LockPosition = dispatched, GuideEpoch = 2 };
            return Task.FromResult(new Phd2ExactLockPositionResult(move.ExpectedCurrentPosition, dispatched,
                dispatched, move.MaximumStepPixels, 0, f.Now.AddSeconds(-3), true, false, false, false, false));
        };
        f.Trace.Handlers["get_GuideSnapshot"] = _ => snapshot;
        f.Trace.Handlers["ValidateCalibrationAsync"] = _ => Task.FromResult(request.Session.Calibration);
        f.Trace.Handlers["BeginFreshResidualObservation"] = _ => new Phd2PostLockGuidingObservation(
            "fixture", 1, 2, 9, dispatched!, 1, f.Now.AddSeconds(-3), MeasuredContinuation: true);
        f.Trace.Handlers["CapturePhd2PlacementGuideWindowAsync"] = a =>
        {
            Assert.NotNull(dispatched);
            Assert.Equal(dispatched, a[3]);
            Assert.Equal(3, a[7]);
            Assert.DoesNotContain(f.Persisted, p => p.Phase == Phd2LockShiftPendingPhase.SettledBudgetLedger);
            return Task.FromResult<IReadOnlyList<Phd2GuidingResidualState>>(request.InitialWindow.Select((sample, i) =>
            {
                var hash = new string((char)('D' + i), 64);
                var time = f.Now.AddSeconds(i - 2);
                return sample with
                {
                    Measurement = sample.Measurement with { GuideStar = dispatched!,
                        TargetCentroid = new(sample.Measurement.RecognizedSlitAcquisitionPoint.X + 3 + i * .1,
                            sample.Measurement.RecognizedSlitAcquisitionPoint.Y), CapturedUtc = time, FrameSha256 = hash },
                    Frame = sample.Frame with { Sha256 = hash, EventSequence = 10 + i, TriggerGuideFrame = 10 + i,
                        GuideStepUtc = time, CompletedUtc = time },
                };
            }).ToArray());
        };
        f.Trace.Handlers["CreateCalibrationResidualEvidence"] = _ => null;
        f.Trace.Handlers["SelectPhd2CalibrationQuality"] = _ => new Phd2CalibrationCandidateSelection(
            Phd2CalibrationSelectionPurpose.LockShift, request.Session.Quality, [request.Session.Quality]);
        f.Trace.Handlers["BuildPhd2LockShiftQualification"] = _ => request.Session.Qualification;
        var result = await f.Workflow.ConvergeAsync(f.Context, request, default);
        Assert.Equal("PHD2_SLIT_PRECISION_WARNING_PROBE_AUTHORIZED", result.Gate.Code);
        Assert.Equal(0, result.Gate.Metrics!["preciseSlitPlacementProven"]);
        Assert.Single(f.Trace.Calls, c => c == "SetExactLockPositionAsync");
        Assert.Single(f.Trace.Calls, c => c == "CapturePhd2PlacementGuideWindowAsync");
        Assert.DoesNotContain("ReturnPhd2LockToOriginAsync", f.Trace.Calls);
        Assert.Equal(request.Ledger.StartedUtc, f.Persisted[^1].StartedUtc);
        Assert.InRange(f.Persisted[^1].CumulativeCommandedPixels, 47.12, 47.13);
        Assert.Equal(new string('F', 64), f.Persisted[^1].LastAcceptedFrameSha256);
        var atr = new AtrWorkflowFixture(1);
        Assert.True((await atr.Science.SelectExposureAsync(atr.Context, default)).CanAdvance);
        Assert.True((await atr.Science.RunAsync(atr.Context, default)).CanAdvance);
        Assert.Equal(1, atr.Science.AcceptedFrames);
    }

    [Theory]
    [InlineData("strict")] [InlineData("incomplete")] [InlineData("not-useful")]
    [InlineData("uncertain-identity")] [InlineData("incoherent")]
    public async Task ShortenedApproachNeverDispatchesWithoutExplicitUsefulWholeWindow(string failure)
    {
        var f = new Phd2ConvergenceFixture(warning: failure != "strict");
        var request = f.V460Request();
        var window = request.InitialWindow.ToArray();
        if (failure == "incomplete") window = [window[^1]];
        if (failure == "not-useful") window = window.Select(s => s with { Measurement = s.Measurement with
            { TargetCentroid = s.Measurement.TargetCentroid with { X = s.Measurement.TargetCentroid.X + 20 } } }).ToArray();
        if (failure == "uncertain-identity") window[0] = window[0] with
            { Measurement = window[0].Measurement with { TargetIdentityConfirmed = false } };
        if (failure == "incoherent") window[0] = window[0] with
            { Measurement = window[0].Measurement with { FrameSha256 = window[^1].Measurement.FrameSha256 } };
        request = request with { InitialWindow = window,
            Session = request.Session with { LastMeasurement = window[^1] } };
        // An unusable motion window may now wait at the unchanged lock, but
        // must still stop at the original deadline without dispatching motion.
        f.Trace.Handlers["CapturePhd2PlacementGuideWindowAsync"] = _ =>
        {
            f.ClockNow = f.Now.AddHours(1);
            return Task.FromResult<IReadOnlyList<Phd2GuidingResidualState>>(window);
        };
        var result = await f.Workflow.ConvergeAsync(f.Context, request, default);
        Assert.False(result.CanAdvance);
        Assert.DoesNotContain("SetExactLockPositionAsync", f.Trace.Calls);
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
    internal DateTimeOffset ClockNow;
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
                UpdatedUtc = Now, CreatedUtc = Now,
                CumulativeCommandedPixels = ledger.CumulativeCommandedPixels,
                LastAcceptedFrameSha256 = ledger.LastAcceptedFrameSha256,
            };
        };
        Trace.Handlers["PersistPendingAsync"] = a => { Persisted.Add((Phd2LockShiftPendingState)a[1]!); return Task.CompletedTask; };
        ClockNow = Now;
        Workflow = new(new(warning, false), port, new FixedClock(() => ClockNow));
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
    internal Phd2SlitConvergenceRequest V460Request()
    {
        var request = Request([25, 25, 26]);
        // Immutable numerical samples from 00215/00217/00219 of the V460 Cyg run;
        // timestamps and provenance are rebased to the deterministic offline fixture.
        Phd2Point[] guide = [new(452.96, 818.745), new(453.137, 820.308), new(454.07, 820.549)];
        Phd2Point[] target = [new(842.6492693110647, 430.83089770354906),
            new(842.1316872427983, 432.56584362139915), new(843.6133333333333, 432.52444444444444)];
        var anchor = new Phd2Point(817.5824596064429, 430.00151301434863);
        var window = request.InitialWindow.Select((s, i) => s with
        {
            Measurement = s.Measurement with { GuideStar = guide[i], TargetCentroid = target[i],
                RecognizedSlitAcquisitionPoint = anchor, CapturedUtc = Now.AddSeconds(i - 5) },
            RuntimeSlitLocal = slit with { AcquisitionPoint = new(anchor.X, anchor.Y), AngleDegrees = 90 },
        }).ToArray();
        return request with
        {
            InitialWindow = window, Session = request.Session with { LastMeasurement = window[^1] },
            Ledger = request.Ledger with { OriginLockPosition = new(475.77, 825.45),
                CurrentLockPosition = new(451.4, 819.88), AttemptsUsed = 1, CumulativeCommandedPixels = 25 },
            Preset = request.Preset with { MaximumStagePixels = 25, MaximumAttempts = 8,
                MaximumStageSeconds = 30, LockVerificationTolerancePixels = 1,
                TargetOnSlitTolerancePixels = 2, MaximumGuideLockResidualPixels = 3, MaximumResidualGrowthPixels = 3 },
        };
    }
    private sealed class FixedClock(Func<DateTimeOffset> now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now(); }
}
