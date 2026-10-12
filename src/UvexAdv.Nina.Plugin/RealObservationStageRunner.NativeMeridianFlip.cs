using System.Diagnostics;
using System.IO;
using NINA.Core.Enum;
using UvexAdv.Nina.Plugin.SequenceItems;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    internal NativeMeridianSession? NativeMeridian { get; set; }
    private ObservationContext? meridianBoundaryContext;
    private string? nativeMeridianOwnerBoundaryFailure;
    private NativeMeridianStageBoundary? nativeMeridianStageBoundary;
    private ObservationContext? nativeMeridianOperationContext;
    private sealed class NativeMeridianBoundaryException : Exception;
    private double MeridianWorkerBoundarySeconds => !configuration.Qhy.SynchronizedPhotometryEnabled ? 0 :
        Math.Max(configuration.Qhy.PhotometryExposureSeconds,
            configuration.Qhy.ParallelFilterSequence.Select(x => x.ExposureSeconds).DefaultIfEmpty(0).Max());
    private string NativeMeridianJournalPath => Path.Combine(SlitPlacementObservationsRoot(), "control", "native-meridian-flip.json");

    private StageResult MeridianSegmentBoundary() => Passed("NATIVE_MERIDIAN_SEGMENT_BOUNDARY",
        "本段在取场/入缝/保存帧边界结束；不是目标完成。确认原所有者停止后交接侧别，重新定位/入缝/导星。");

    private void CheckNativeMeridianOperationBoundary(ObservationContext context,
        bool destinationWouldChangeSide = false, PierSide? savedSide = null, bool allowScheduled = true)
    {
        var stage = (ObservationStage)Volatile.Read(ref executingStage);
        if (NativeMeridian is null || NativeMeridian.Requested || nativeMeridianStageBoundary is null ||
            stage is ObservationStage.ValidateNightSetup or ObservationStage.SlewToCatalogTarget or ObservationStage.FinalizeObservation ||
            configuration.SequencePlan?.DeferObservatoryCloseout != true || configuration.Environment.WeakSupervisionEnabled)
            return;
        if (stage == ObservationStage.RunScienceBlock && (savedAtrFrames >= configuration.Atr.ScienceFrameCount ||
            AtrScienceFrameQualityPolicy.RemainingAttemptGate(savedAtrFrames, attemptedAtrFrames, configuration.Atr) is not null))
            return; // A finished science block needs no additional segment.
        var mount = telescopeMediator.GetInfo();
        // Do not interrupt a moving owner or an already-open ATR shutter.
        // Owner task completion and normal exposure saving remain authoritative.
        if (!mount.Connected || mount.Slewing || mount.AtPark || mount.AtHome || !mount.TrackingEnabled ||
            mount.SideOfPier is not (PierSide.pierEast or PierSide.pierWest) || cameraMediator.GetInfo().IsExposing)
            return;
        if (ValidateImmediatePhysicalActionGates(context).Disposition != GateDisposition.Passed || SequenceStopGate() is not null)
            return; // Environment/cancellation/deadline keeps precedence.
        NativeMeridian.VerifyScope?.Invoke();
        if (!NativeMeridian.RequestAtOperationBoundary(mount.SideOfPier, MeridianWorkerBoundarySeconds,
                destinationWouldChangeSide, savedSide, allowScheduled)) return;
        meridianBoundaryContext = context;
        Report(NativeMeridian.ObservedSideChange
            ? "赤道仪已报告换侧：停止旧取场/入缝，核验后重新定位同一目标，不重复翻转。"
            : "已到中天交接边界：停止本段，交由原生翻转后重新定位同一目标。");
        nativeMeridianStageBoundary.Interrupt();
    }

    private bool RequestNativeMeridianBoundary(ObservationContext context, double nextExposure)
    {
        if (NativeMeridian?.RequestAtFrameBoundary(nextExposure + MeridianWorkerBoundarySeconds) != true) return false;
        meridianBoundaryContext = context;
        Report("中天翻转：不再开始新曝光；保存的科学帧继续计数，正在结束本段并等待两路采集停止。");
        PublishAcquisitionProgress(context, "MeridianBoundary");
        return true;
    }

    internal void RecordMeridianSegment(NativeTargetSession session, string runId) =>
        session.RecordSegment(runId, savedAtrFrames, attemptedAtrFrames, reusedAtrProbeFrames, acceptedAtrExposureSeconds);

    internal async Task PerformNativeMeridianFlipAsync(SpectroscopyMeridianFlipTrigger trigger,
        NativeMeridianSession session, string lockedTriggerConfiguration, CancellationToken token)
    {
        var context = meridianBoundaryContext ?? throw new InvalidOperationException("没有完成保存的翻转边界。");
        if (nativeMeridianOwnerBoundaryFailure is not null) throw new InvalidOperationException(nativeMeridianOwnerBoundaryFailure);
        if (configuration.SequencePlan?.DeferObservatoryCloseout != true || configuration.Environment.WeakSupervisionEnabled)
            throw new InvalidOperationException("光谱翻转仅在持有设备执行权和完整安全链的整夜序列中执行。");
        if (NativeMeridianJournalStore.PendingGate(NativeMeridianJournalPath) is { } pending)
            throw new InvalidOperationException(pending.Message);
        _ = session.RemainingPlan(); // No new episode when the original exposure budget is exhausted.
        using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        idleTimeout.CancelAfter(TimeSpan.FromSeconds(75));
        var idle = await ConfirmSequenceIdleAsync(idleTimeout.Token, requireSettledMotion: false).ConfigureAwait(false);
        if (idle.Count != 0) throw new InvalidOperationException(string.Join(" ", idle));
        // A boundary at stage entry can precede loading the independent transform
        // ledger. Read its durable authority as well; never retire it with G3/PHD2.
        var independent = await ResolveSlitPlacementPendingAsync(context, idleTimeout.Token).ConfigureAwait(false);
        if (independent.Error is { } independentError)
            throw new InvalidOperationException($"{independentError.Code}: {independentError.Message}");
        if (pendingSlitPlacement is { Phase: not SlitPlacementPendingPhase.SettledBudgetLedger } ||
            independent.Resolution?.State is { Phase: not SlitPlacementPendingPhase.SettledBudgetLedger })
            throw new InvalidOperationException("独立变换机构运动尚未结清，不能借翻转退役。");
        CheckFlipPrerequisites();
        var before = session.BeforeSide ?? trigger.RequestedSide ?? PierSide.pierUnknown;
        var expectedBeforeAction = session.ObservedSideChange ? session.AfterSide : before;
        if (before is not (PierSide.pierEast or PierSide.pierWest) ||
            telescopeMediator.GetInfo().SideOfPier != expectedBeforeAction)
            throw new InvalidOperationException("翻转前没有可核验的镜筒侧。");
        var wait = session.ObservedSideChange ? TimeSpan.Zero : trigger.WaitBeforeFlip;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        if (wait > TimeSpan.FromHours(2) || !double.IsFinite(trigger.SettleSeconds) || trigger.SettleSeconds is < 0 or > 300)
            throw new InvalidOperationException("翻转等待/稳定时间超出可执行范围。");
        var started = DateTimeOffset.UtcNow;
        async Task Journal(string phase, string after = "")
        {
            await NativeMeridianJournalStore.SaveAsync(NativeMeridianJournalPath,
                new(context.Plan.ObservationRunId, phase, before.ToString(), after, DateTimeOffset.UtcNow,
                    session.SegmentRunIds.ToArray(), session.ObservedSideChange ? "ObservedSideChange" : "Scheduled"), token).ConfigureAwait(false);
            trigger.SetStatus($"翻转：{phase}；已接受 {session.Accepted}/{session.Original.ScienceFrames} 张，原始帧保留。");
        }
        void CheckFlipPrerequisites()
        {
            token.ThrowIfCancellationRequested();
            session.VerifyScope?.Invoke();
            if (SequenceStopGate() is { } stop) throw new InvalidOperationException(stop.Message);
            if (trigger.ConfigurationKey != lockedTriggerConfiguration || !trigger.Validate() ||
                trigger.Status == SequenceEntityStatus.DISABLED)
                throw new InvalidOperationException("翻转触发器配置在本目标执行中改变；不继续运动。");
            var gate = ValidateImmediatePhysicalActionGates(context);
            if (gate.Disposition != GateDisposition.Passed) throw new InvalidOperationException($"{gate.Code}: {gate.Message}");
            if (!cameraMediator.GetInfo().Connected || cameraMediator.GetInfo().IsExposing ||
                activeQhyJobs.Count != 0 || pendingQhyRequests.Count != 0)
                throw new InvalidOperationException("翻转期间曝光所有者不再满足空闲条件。");
            if (!telescopeMediator.GetInfo().Connected) throw new InvalidOperationException("翻转期间赤道仪断开。");
        }
        async Task DelayChecked(TimeSpan duration)
        {
            var timer = Stopwatch.StartNew();
            do
            {
                CheckFlipPrerequisites();
                await Task.Delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            } while (timer.Elapsed < duration);
        }
        await Journal("OwnersIdle").ConfigureAwait(false);
        try
        {
            // This explicit meridian journal, terminal segment and fresh owner
            // proof are all required before retiring old-side CONTROL handles.
            // Original files/counters remain archived; return is NOT asserted.
            var archives = await GuideStateBoundaryRetirement.RetireMeridianSegmentAsync(
                SlitPlacementObservationsRoot(), context.Plan.ObservationRunId,
                session.TargetPlan!.ObservationRunId, NativeMeridianJournalPath,
                ReadTerminalGuideWitnessAsync, token).ConfigureAwait(false);
            pendingPhd2LockShift = null;
            durableG3AcquisitionMotion = null;
            pendingG3SearchReturn = null;
            await WriteAuditBestEffortAsync("native-meridian-old-side-retired", new
            { archives, returnVerified = false, sameTargetRetry = false, budgetsRewritten = false }).ConfigureAwait(false);
            NativeMeridianMountReadback ReadMount()
            {
                var info = telescopeMediator.GetInfo();
                return new(info.Connected && !info.IsPulseGuiding, info.Slewing, info.TrackingEnabled,
                    info.AtPark, info.AtHome, info.SideOfPier);
            }
            PierSide afterSide;
            if (session.ObservedSideChange)
            {
                // A driver/ordinary slew has already changed the orientation.
                // Verify it without another flip, stop-tracking or blind return.
                afterSide = await NativeMeridianMountOperation.VerifyObservedChangeAsync(before, session.AfterSide!.Value,
                    ReadMount, CheckFlipPrerequisites,
                    _ => DelayChecked(TimeSpan.FromSeconds(Math.Max(2, trigger.SettleSeconds))), token).ConfigureAwait(false);
            }
            else
            {
                // Reuse N.I.N.A.'s timing and mount API, not its main-camera
                // recenter/autofocus workflow (ATR sees a spectrum, not a star field).
                if (!telescopeMediator.SetTrackingEnabled(false)) throw new InvalidOperationException("未确认停止跟踪。");
                await DelayChecked(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (telescopeMediator.GetInfo().TrackingEnabled || telescopeMediator.GetInfo().Slewing)
                    throw new InvalidOperationException("停止跟踪/运动读回未确认。");
                await Journal("WaitingForMeridian").ConfigureAwait(false);
                await DelayChecked(wait - (DateTimeOffset.UtcNow - started)).ConfigureAwait(false);
                CheckFlipPrerequisites();
                if (!telescopeMediator.SetTrackingEnabled(true)) throw new InvalidOperationException("未确认恢复跟踪。");
                await DelayChecked(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (!telescopeMediator.GetInfo().TrackingEnabled || telescopeMediator.GetInfo().Slewing ||
                    telescopeMediator.GetInfo().SideOfPier != before)
                    throw new InvalidOperationException("翻转前读回改变；没有下发翻转。");
                await Journal("CommandPending").ConfigureAwait(false);
                afterSide = await NativeMeridianMountOperation.ExecuteAsync(before, ReadMount,
                    ct => telescopeMediator.MeridianFlip(TargetCoordinates(context.Plan), ct),
                    () => telescopeMediator.StopSlew(), CheckFlipPrerequisites,
                    _ => DelayChecked(TimeSpan.FromSeconds(Math.Max(2, trigger.SettleSeconds))), token).ConfigureAwait(false);
            }
            // Dome synchronization is provided by the same N.I.N.A. follower.
            if (domeMediator.GetInfo().Connected && domeMediator.GetInfo().CanSetAzimuth)
            {
                using var sync = CancellationTokenSource.CreateLinkedTokenSource(token);
                sync.CancelAfter(TimeSpan.FromMinutes(2));
                if (!domeFollower.IsFollowing) throw new InvalidOperationException("圆顶未启用 N.I.N.A. 跟随，翻转后不能确认开口对准。");
                await domeFollower.WaitForDomeSynchronization(sync.Token).ConfigureAwait(false);
            }
            CheckFlipPrerequisites();
            var finalMount = telescopeMediator.GetInfo();
            if (finalMount.SideOfPier != afterSide || finalMount.Slewing || !finalMount.TrackingEnabled)
                throw new InvalidOperationException("圆顶同步期间赤道仪状态改变；不开始下一段。");
            await Journal(session.ObservedSideChange ? "SideChangeVerified" : "MountVerified", afterSide.ToString()).ConfigureAwait(false);
            await WriteAuditBestEffortAsync("native-meridian-mount-verified", new
            {
                context.Plan.ObservationRunId, before = before.ToString(), after = afterSide.ToString(),
                segments = session.SegmentRunIds, session.Accepted, session.Attempts,
                oldOpticalEvidenceReusable = false, scienceAuthorized = false,
                flipCommandSent = !session.ObservedSideChange,
            }).ConfigureAwait(false);
            session.ConfirmFlip();
            trigger.SetStatus("翻转已核验；下一段必须重新定位、测缝、入缝、导星并验证曝光，尚未恢复科学曝光。");
        }
        catch
        {
            trigger.SetStatus("翻转未完成；停止本夜，不自动重试。检查翻转记录与设备读回。");
            throw;
        }
    }
}
