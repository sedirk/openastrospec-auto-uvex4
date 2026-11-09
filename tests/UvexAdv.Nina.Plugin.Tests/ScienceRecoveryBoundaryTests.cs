using System.Reflection;
using System.Runtime.CompilerServices;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ScienceRecoveryBoundaryTests
{
    [Fact]
    public void AtrClockCreditRequiresSettledProofCompletedCaptureAndKeepsGuideQualityCheck()
    {
        var before = Source.IndexOf("var exposureLedger = await ReadSettledAtrExposureLedgerAsync(", StringComparison.Ordinal);
        var capture = Source.IndexOf("await imagingMediator.CaptureImage(sequence", before, StringComparison.Ordinal);
        var credit = Source.IndexOf("await RecordAtrExposureClockAsync(", capture, StringComparison.Ordinal);
        var image = Source.IndexOf("await exposure.ToImageData(", credit, StringComparison.Ordinal);
        var continuity = Source.IndexOf("atr-exposure-guide-continuity", image, StringComparison.Ordinal);
        Assert.True(before >= 0 && capture > before && credit > capture && image > credit && continuity > image);
        Assert.Contains("if (exposure is null)", Source[capture..credit]);
        Assert.Contains("throw new InvalidOperationException(\"ATR_EXPOSURE_EMPTY", Source[capture..credit]);
        Assert.Contains("if (!IsGuidingStable())", Source[before..capture]);
        var recovery = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.AdaptiveGuidingRecovery.cs"));
        Assert.Contains("Phd2ScienceContinuationPolicy.MayVerifySettledGuideInPlace(original.Gate.Code)", recovery);
        Assert.True(recovery.IndexOf("TryVerifyScienceInPlaceAsync", StringComparison.Ordinal) <
            recovery.IndexOf("WaitForSameFieldGuideSelfRecoveryAsync", StringComparison.Ordinal));
        Assert.True(recovery.IndexOf("WaitForSameFieldGuideSelfRecoveryAsync", StringComparison.Ordinal) <
            recovery.IndexOf("EnsurePhdStoppedForAutomaticRebuildAsync", StringComparison.Ordinal));
        Assert.True(recovery.IndexOf("CheckScienceRebuildBudgetAsync", StringComparison.Ordinal) <
            recovery.IndexOf("adaptiveCalibrationPending = action", StringComparison.Ordinal));
        Assert.Contains("CheckScienceRebuildBudgetAsync(", Source);
        Assert.Contains("replacementBudget is not null", Source);
        var accounting = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.ScienceContinuation.cs"));
        Assert.Contains("pendingPhd2LockShift is not null", accounting);
        Assert.Contains("CanObserveAtSettledLock", accounting);
        Assert.Contains("ValidateCurrentPhd2LockLedgerBinding", accounting);
        Assert.Contains("motionBudgetReset = false, scienceAccepted = false", accounting);
        Assert.Contains("Phd2ScienceContinuationPolicy.RebuildBudgetGate(ledger, DateTimeOffset.UtcNow, sourceGate, binding)", accounting);
    }

    [Theory]
    [InlineData(ObservationStage.SelectAtrExposure)]
    [InlineData(ObservationStage.RunScienceBlock)]
    public async Task ActualRunnerAtrInvalidationKeepsPhotometryOwnershipAndFrameCounters(ObservationStage stage)
    {
        // Exercise the production invalidator, without invoking the constructor
        // (which creates real owner clients). This path only invalidates fields;
        // no device object, UI, socket, exposure or service is instantiated.
        var runner = (RealObservationStageRunner)RuntimeHelpers.GetUninitializedObject(typeof(RealObservationStageRunner));
        var job = Guid.NewGuid();
        Set(runner, "photometryJobId", (Guid?)job);
        Set(runner, "qhyPhotometryAttempt", 3);
        var f = new AtrWorkflowFixture();
        await f.Science.SelectExposureAsync(f.Context, default);
        f.Port.NextMetrics = m => m with { GuidingStable = false };
        await f.Science.RunAsync(f.Context, default);
        Set(runner, "atrProbe", f.Probe);
        Set(runner, "atrScience", f.Science);

        typeof(RealObservationStageRunner).GetMethod("InvalidateStageState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runner, new object[] { stage });

        Assert.Equal(job, Get<Guid?>(runner, "photometryJobId"));
        Assert.Equal(3, Get<int>(runner, "qhyPhotometryAttempt"));
        Assert.Null(f.Probe.SelectedExposureSeconds);
        Assert.True(f.Probe.RequiresReprobe);
        Assert.Equal(3, f.Science.AcceptedFrames);
        Assert.Equal(3, f.Science.AttemptedFrames);
        Assert.Equal(2, f.Science.RetainedFrames);
    }

    [Fact]
    public void CompletedGuideContinuityChangeIsSavedWithWarningBeforeAnyNextExposureGate()
    {
        var science = WorkflowSource.Science;
        var save = science.IndexOf("await port.SaveAtrImageAsync(", StringComparison.Ordinal);
        var retained = science.IndexOf("retainedAtrScienceFrames++;", StringComparison.Ordinal);
        var qualityRejection = science.IndexOf("if (quality.Disposition != GateDisposition.Passed)", StringComparison.Ordinal);
        var accepted = science.IndexOf("savedAtrFrames++;", StringComparison.Ordinal);
        Assert.True(save >= 0 && retained > save && qualityRejection > retained && accepted > qualityRejection);
        Assert.Contains("quality.Disposition == GateDisposition.Passed", science, StringComparison.Ordinal);
        Assert.Contains("AtrScienceFrameQualityPolicy.Evaluate(metrics, configuration)", WorkflowSource.Science, StringComparison.Ordinal);
        Assert.DoesNotContain("quality.Code == \"GUIDING_UNSTABLE\"", science, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyRecoveryDoesNotSwallowOperatorRestartOrPhysicalEvidenceGates()
    {
        var recovery = Slice("private async Task<(bool Retry, StageResult Result)> PrepareAutomaticStageRecoveryAsync(",
            "private async Task<GateResult> EnsurePhdStoppedForAutomaticRebuildAsync(");
        Assert.Contains("catch (ResumeStageRestartException) { throw; }", recovery, StringComparison.Ordinal);
        Assert.Contains("catch (PhysicalActionGateException ex)", recovery, StringComparison.Ordinal);
        Assert.Contains("return (false, new StageResult(ex.Gate));", recovery, StringComparison.Ordinal);
        Assert.Contains("PHD2_SCIENCE_IN_PLACE_CHECK_FAILED", recovery, StringComparison.Ordinal);
        var physicalCatch = Slice("catch (PhysicalActionGateException ex)\n", "catch (OperationCanceledException)\n");
        Assert.Contains("catch (ResumeStageRestartException)", physicalCatch, StringComparison.Ordinal);
        Assert.Contains("continue;", physicalCatch, StringComparison.Ordinal);
    }

    [Fact]
    public void PostExposureStaleSettleResumesSameLockBeforeAnyRebuildOrReselection()
    {
        var continuation = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Sources",
            "RealObservationStageRunner.ScienceContinuation.cs"));
        var start = continuation.IndexOf(
            "private async Task<bool> TryVerifyScienceInPlaceAsync(",
            StringComparison.Ordinal);
        var end = continuation.IndexOf(
            "private async Task<Phd2LockShiftPendingState?> ReadSettledAtrExposureLedgerAsync(",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var inPlace = continuation[start..end];

        Assert.Contains("CanResettleAfterExposureAtSettledLock", inPlace, StringComparison.Ordinal);
        Assert.Contains("ResettleAtCurrentLockAsync", inPlace, StringComparison.Ordinal);
        Assert.Contains("Phd2SameLockSettleRequest", inPlace, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectGuideStarAsync", inPlace, StringComparison.Ordinal);
        Assert.DoesNotContain("GuideAndSettleAsync", inPlace, StringComparison.Ordinal);
        Assert.True(inPlace.IndexOf("ResettleAtCurrentLockAsync", StringComparison.Ordinal) <
            inPlace.IndexOf("VerifyWindSampledGuidingBeforeAtrAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void ThinCloudSelfRecoveryWaitsOnSameFieldWithoutStopSelectionOrMotion()
    {
        var recovery = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Sources",
            "RealObservationStageRunner.AdaptiveGuidingRecovery.cs"));
        var start = recovery.IndexOf(
            "private async Task<bool> WaitForSameFieldGuideSelfRecoveryAsync(",
            StringComparison.Ordinal);
        var end = recovery.IndexOf(
            "private async Task<bool> TryReacquireSameFieldGuideWithoutMotionAsync(",
            start,
            StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var wait = recovery[start..end];

        Assert.Contains("IsSameFieldGuideActiveAfterLoss", wait, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(TimeSpan.FromSeconds(1)", wait, StringComparison.Ordinal);
        Assert.DoesNotContain("StopCapture", wait, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectGuideStarAsync", wait, StringComparison.Ordinal);
        Assert.DoesNotContain("GuideAndSettleAsync", wait, StringComparison.Ordinal);
        Assert.DoesNotContain("CaptureSingleFrame", wait, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestNativeMeridianBoundary", wait, StringComparison.Ordinal);
    }

    [Fact]
    public void ScienceBudgetIsCheckedBeforeReprobeAndAfterRejectionBeforeRecovery()
    {
        var science = WorkflowSource.Science;
        var entry = science.IndexOf("RemainingAttemptGate(", StringComparison.Ordinal);
        var probe = science.IndexOf("if (probe.RequiresReprobe)", StringComparison.Ordinal);
        var loop = science.IndexOf("while (savedAtrFrames", StringComparison.Ordinal);
        var loopGate = science.IndexOf("RemainingAttemptGate(", loop, StringComparison.Ordinal);
        var loopProbe = science.IndexOf("if (probe.RequiresReprobe)", loop, StringComparison.Ordinal);
        var exhausted = science.IndexOf("var exhaustedAttempts", StringComparison.Ordinal);
        var accepted = science.IndexOf("savedAtrFrames++;", exhausted, StringComparison.Ordinal);
        Assert.True(entry >= 0 && entry < probe && loopGate > loop && loopGate < loopProbe);
        Assert.True(exhausted > loopProbe && accepted > exhausted);
    }

    [Fact]
    public void TerminalScienceAttemptsStillReconcileDebtButNeverEnterResumeOrAutomaticRebuild()
    {
        var execute = Slice("public override async Task<StageResult> ExecuteStageAsync(",
            "private async Task<(bool Retry, StageResult Result)> PrepareAutomaticStageRecoveryAsync(");
        var debt = execute.IndexOf("await RecoverDurableSlitPlacementBeforeStageAsync(", StringComparison.Ordinal);
        var terminal = execute.IndexOf("RemainingAttemptGate(", StringComparison.Ordinal);
        var resume = execute.IndexOf("await RecoverInterruptedStageAsync(", StringComparison.Ordinal);
        Assert.True(debt >= 0 && terminal > debt && resume > terminal);
        var automatic = Slice("private async Task<(bool Retry, StageResult Result)> PrepareAutomaticStageRecoveryAsync(",
            "private async Task<GateResult> EnsurePhdStoppedForAutomaticRebuildAsync(");
        Assert.True(automatic.IndexOf("RemainingAttemptGate(", StringComparison.Ordinal) <
            automatic.IndexOf("switch (plan.Action)", StringComparison.Ordinal));
    }

    private static readonly string Source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs")).Replace("\r\n", "\n");
    private static string Slice(string start, string end)
    {
        var from = Source.IndexOf(start, StringComparison.Ordinal);
        var to = Source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from);
        return Source[from..to];
    }

    private static void Set(object instance, string name, object? value) => Field(name).SetValue(instance, value);
    private static T? Get<T>(object instance, string name) => (T?)Field(name).GetValue(instance);
    private static FieldInfo Field(string name) => typeof(RealObservationStageRunner).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
