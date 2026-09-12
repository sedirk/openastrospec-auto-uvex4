using System.Diagnostics;
using System.IO;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private async Task<StageResult?> VerifyPriorCrossPierReturnOriginAsync(
        ObservationContext context, ObservationStage stage, G3AcquisitionMotionState state,
        string path, CancellationToken cancellationToken)
    {
        const string code = "G3_MOTION_CROSS_PIER_ORIGIN_UNCONFIRMED";
        var settleSeconds = configuration.G3.MotionPostSlewSettleSeconds;
        if (!double.IsFinite(settleSeconds) || settleSeconds <= 0)
            return new StageResult(GateResult.Unknown(code, "A commissioned positive origin readback interval is required; no old return command was sent."), path);

        Report("上轮回程侧别已变化；只读核验是否已经到达保存的绝对坐标，不执行跨侧旧位移。");
        var samples = new List<G3OriginReadback>();
        var watch = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(Math.Min(1, settleSeconds / 2));
        do
        {
            if (samples.Count > 0) await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
            var physical = ValidateImmediatePhysicalActionGates(context);
            if (physical.Disposition != GateDisposition.Passed) return new StageResult(physical, path);
            var reported = telescopeMediator.GetCurrentPosition();
            var mount = telescopeMediator.GetInfo();
            var horizon = ValidateCommandCoordinateHorizon(context, reported, "prior final-return read-only origin check");
            if (horizon.Disposition != GateDisposition.Passed) return new StageResult(horizon, path);
            // Do not require a return motion's old pier side or enable tracking.
            // Stability must be observed, even if tracking is currently off.
            var idle = mount.Connected && !mount.Slewing && !mount.IsPulseGuiding &&
                phd2.Snapshot.AppState is Phd2AppState.Stopped or Phd2AppState.Selected or Phd2AppState.Looping;
            samples.Add(new(DateTimeOffset.UtcNow, NormalizeDegrees(reported.RADegrees),
                reported.Dec, reported.Epoch.ToString(), mount.SideOfPier.ToString(), idle));
        } while (watch.Elapsed.TotalSeconds < settleSeconds + 2 &&
                 (samples.Count < 3 || (samples[^1].CapturedUtc - samples[0].CapturedUtc).TotalSeconds < settleSeconds));

        var now = DateTimeOffset.UtcNow;
        var gate = G3PriorReturnOriginPolicy.Evaluate(state, context.Plan.ObservationRunId, stage,
            samples, configuration.G3.WcsFreshSolveAuthorizationResidualArcseconds, settleSeconds,
            MountCommandArrivalToleranceArcseconds, now);
        var proofPath = await PublishRunJsonEvidenceAsync("g3-prior-return-origin-readback",
            "Read-only absolute-origin verification of a previous run's final return",
            new { priorState = state, samples, gate, newRunId = context.Plan.ObservationRunId,
                noMountCommandSent = true, opticalTargetAccepted = false, priorBudgetsReset = false },
            path, cancellationToken).ConfigureAwait(false);
        if (gate.Disposition != GateDisposition.Passed) return new StageResult(gate, proofPath);

        // Fail closed if another writer changed the retained intent while we
        // were observing. The exact pre-closure envelope is kept for audit.
        var loaded = await G3AcquisitionMotionStore.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        if (loaded.Error is not null || loaded.State != state)
            return new StageResult(GateResult.Unknown(code, "The prior return record changed during read-only verification; it was not overwritten."), proofPath);
        // Slow evidence IO must not turn a stale window into a current proof.
        now = DateTimeOffset.UtcNow;
        gate = G3PriorReturnOriginPolicy.Evaluate(state, context.Plan.ObservationRunId, stage,
            samples, configuration.G3.WcsFreshSolveAuthorizationResidualArcseconds, settleSeconds,
            MountCommandArrivalToleranceArcseconds, now);
        if (gate.Disposition != GateDisposition.Passed) return new StageResult(gate, proofPath);
        var closed = G3PriorReturnOriginPolicy.CloseVerifiedReturn(state, context.Plan.ObservationRunId,
            stage, samples, configuration.G3.WcsFreshSolveAuthorizationResidualArcseconds,
            settleSeconds, MountCommandArrivalToleranceArcseconds, now);
        File.Copy(path, path + ".origin-verified-" + Guid.NewGuid().ToString("N") + ".json", overwrite: false);
        await G3AcquisitionMotionStore.WriteAtomicAsync(path, closed, cancellationToken).ConfigureAwait(false);
        // Do not PersistG3AcquisitionMotionAsync: that would adopt an old run's
        // lineage into this new run. Its counts, side and clock stay historical.
        lastG3Field = null;
        pendingG3SearchReturn = null;
        Report("已只读确认上轮回程到位并保留原账本；本轮从 Night Setup 重新取证，不复用旧侧别、旧星图或旧位移。");
        return null;
    }
}
