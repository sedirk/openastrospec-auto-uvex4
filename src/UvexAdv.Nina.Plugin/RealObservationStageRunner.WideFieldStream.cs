using System.IO;
using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private QhyWideFieldStreamSession? wideFieldStream;
    private readonly object wideFieldStateGate = new();
    private readonly List<G3FrameMountReadback> wideFieldMountSamples = new();
    private readonly List<QhyG3PairQhySource> wideFieldPoses = new();
    private QhyG3PairQhySource? wideFieldFormalAnchor;
    private string? wideFieldOpticalSha;
    private string? wideFieldPierSide;
    private string? wideFieldCoordinateEpoch;
    private DateTimeOffset wideFieldLastKeyframeAttemptUtc;
    private readonly SemaphoreSlim imageSolverPriorityGate = new(1, 1);
    private readonly object imageSolverPriorityStateGate = new();
    private CancellationTokenSource? activeWideFieldKeyframeSolve;
    private int imageSolverForegroundWaiters;

    private async Task<PlateSolveResult> RunWithImageSolverPriorityAsync(string role,
        Func<CancellationToken, Task<PlateSolveResult>> work, CancellationToken token)
    {
        var background = role == "QHY/GS350 continuous formal keyframe";
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (background)
        {
            lock (imageSolverPriorityStateGate)
            {
                token.ThrowIfCancellationRequested();
                if (imageSolverForegroundWaiters > 0 || !imageSolverPriorityGate.Wait(0))
                    throw new WideFieldKeyframePreemptedException();
                activeWideFieldKeyframeSolve = owned;
            }
        }
        else
        {
            CancellationTokenSource? older;
            lock (imageSolverPriorityStateGate)
            {
                imageSolverForegroundWaiters++;
                older = activeWideFieldKeyframeSolve;
            }
            try { older?.Cancel(); } catch (ObjectDisposedException) { }
            try { await imageSolverPriorityGate.WaitAsync(token).ConfigureAwait(false); }
            finally { lock (imageSolverPriorityStateGate) imageSolverForegroundWaiters--; }
        }
        try
        {
            return await work(owned.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (background && !token.IsCancellationRequested)
        { throw new WideFieldKeyframePreemptedException(); }
        finally
        {
            if (background) lock (imageSolverPriorityStateGate) activeWideFieldKeyframeSolve = null;
            imageSolverPriorityGate.Release();
        }
    }

    private async Task<GateResult> EnsureWideFieldAcquisitionStreamAsync(ObservationContext context,
        DateTimeOffset originalDeadlineUtc, CancellationToken token)
    {
        if (wideFieldStream is not null)
            return wideFieldStream.IsJoined ? GateResult.Warn("QHY_STREAM_ALREADY_RETIRED", "A retired acquisition stream is not restarted automatically.")
                : GateResult.Pass("QHY_STREAM_ALREADY_ACTIVE", "The existing acquisition-only owner job remains active.");
        if (!qhyAvailableForRun || lastQhyAcquisition?.AcceptedFrameId is not { } acceptedId ||
            lastQhySolve is not { IsFormal: true, Result.Success: true } || lastQhyAcceptedFrameMountBinding is null)
            return GateResult.Warn("QHY_STREAM_INITIAL_ANCHOR_UNAVAILABLE", "No completed initial formal QHY witness; ordinary acquisition remains available.");
        var initial = lastQhyAcquisition.Frames.SingleOrDefault(f => f.FrameId == acceptedId);
        var optical = await ReadLocalDualOpticalStateAsync(context, token).ConfigureAwait(false);
        if (initial is null || optical is null)
            return GateResult.Warn("QHY_STREAM_OPTICAL_BINDING_UNAVAILABLE", "Acquisition stream lacks its initial optical context.");
        var remaining = originalDeadlineUtc - DateTimeOffset.UtcNow;
        var exposure = configuration.G3.EffectiveFastSolvePair.QuickQhyExposureSeconds;
        if (!double.IsFinite(exposure) || exposure <= 0) exposure = initial.Settings.ExposureSeconds;
        var subordinateFrames = Math.Min(4096, (int)Math.Floor(remaining.TotalSeconds / exposure));
        if (subordinateFrames < 2)
            return GateResult.Warn("QHY_STREAM_ORIGINAL_BUDGET_EXHAUSTED", "The original deadline cannot contain two acquisition frames; no new allowance was created.");
        wideFieldOpticalSha = optical.ComputeSha256();
        wideFieldPierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
        wideFieldCoordinateEpoch = CaptureG3FrameMountReadback().CoordinateEpoch;
        wideFieldFormalAnchor = new(lastQhyAcquisition, initial, lastQhySolve, lastQhyAcceptedFrameMountBinding,
            QhyG3SolvePairSource.ReusedFreshQhySolve);
        lock (wideFieldStateGate)
        {
            wideFieldMountSamples.Add(CaptureG3FrameMountReadback());
            wideFieldPoses.Add(wideFieldFormalAnchor);
        }
        var settings = initial.Settings;
        var request = new AcquisitionJobRequest(context.Plan.ObservationRunId, context.Plan.Target.Name + " / continuous acquisition witness",
            [exposure], settings.Gain, settings.Offset, MaximumAttempts: 1, BinningX: settings.BinningX, BinningY: settings.BinningY,
            ReadoutMode: settings.ReadoutMode, FilterName: settings.FilterName, TargetTemperatureC: settings.TargetTemperatureC,
            QualityThresholds: configuration.Qhy.QualityThresholds, RoiX: settings.RoiX, RoiY: settings.RoiY,
            RoiWidth: settings.RoiWidth, RoiHeight: settings.RoiHeight, BitDepth: settings.BitDepth, UsbTraffic: settings.UsbTraffic,
            ClientRequestId: context.Plan.ObservationRunId + ":continuous-wide-acquisition", TargetRightAscensionDegrees: context.Plan.Target.RightAscensionDegrees,
            TargetDeclinationDegrees: context.Plan.Target.DeclinationDegrees, ControlLeaseSeconds: 15,
            NightSetupId: nightSetup!.Value.NightSetupId,
            WideFieldStream: new(originalDeadlineUtc, subordinateFrames));
        // Foreground-only full gate may pause, repair clock or enter meridian
        // recovery. Background checkpoints below are deliberately read-only.
        await RequireImmediatePhysicalActionGatesAsync(context, token).ConfigureAwait(false);
        pendingQhyRequests[request.ClientRequestId!] = new PendingQhyRequest(context.Plan.ObservationRunId,
            QhyJobKind.Acquisition, request.ClientRequestId!, request);
        var startReturned = false;
        try
        {
            wideFieldStream = await QhyWideFieldStreamSession.StartAsync(qhy, request,
                t => WideFieldStreamCheckpointAsync(context, t),
                (job, _) => { RegisterActiveQhyJob(job); return Task.CompletedTask; },
                (job, frame, t) => AnalyzeWideFieldStreamFrameAsync(context, job, frame, t), token).ConfigureAwait(false);
            startReturned = true;
        }
        finally { if (startReturned) pendingQhyRequests.TryRemove(request.ClientRequestId!, out _); }
        await WriteAuditBestEffortAsync("qhy-wide-stream-start", new { request.WideFieldStream,
            enabled = wideFieldStream is not null, capacity = 1, latestWins = true, acquisitionOnly = true,
            budgetReset = false, ownerChanged = false }).ConfigureAwait(false);
        return wideFieldStream is null
            ? GateResult.Warn("QHY_STREAM_OWNER_CAPABILITY_UNAVAILABLE", "The configured existing owner has not advertised bounded-stream support; no stream job was dispatched.")
            : GateResult.Pass("QHY_STREAM_STARTED", "One bounded acquisition-only stream started through the existing owner and transport.");
    }

    private Task WideFieldStreamCheckpointAsync(ObservationContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (SequenceStopGate() is { } stop) throw new PhysicalActionGateException(stop);
        var immediate = ValidateImmediatePhysicalActionGates(context);
        if (immediate.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(immediate);
        if (Volatile.Read(ref resumeRecoveryRequired) != 0)
            throw new QhyG3OptionalOpticalException("QHY stream retired at recovery boundary; no background recovery was started.");
        var focus = c11RunFocusLock.Validate(ReadC11MainFocusOwner(), nightSetup!.Value);
        if (focus.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(focus);
        var sample = CaptureG3FrameMountReadback();
        if (sample.PierSide != wideFieldPierSide || sample.CoordinateEpoch != wideFieldCoordinateEpoch)
            throw new QhyG3OptionalOpticalException("QHY stream owner pointing epoch/pier changed; a new context must be explicitly started.");
        if (durableG3AcquisitionMotion is { Phase: G3AcquisitionMotionPhase.AwaitingFreshSolve } pending)
        {
            var reserved = G3AcquisitionReturnTimePolicy.Plan(pending, DateTimeOffset.UtcNow).Gate;
            if (reserved.Disposition != GateDisposition.Passed) throw new QhyG3OptionalOpticalException(reserved.Code + ": " + reserved.Message);
        }
        lock (wideFieldStateGate)
        {
            wideFieldMountSamples.Add(sample);
            if (wideFieldMountSamples.Count > 2048) wideFieldMountSamples.RemoveRange(0, wideFieldMountSamples.Count - 2048);
        }
        return Task.CompletedTask;
    }

    private async Task<GateResult> StopWideFieldAcquisitionStreamAsync(CancellationToken token)
    {
        if (wideFieldStream is null) return GateResult.Pass("QHY_STREAM_NOT_ACTIVE", "No continuous acquisition job was started.");
        var gate = await wideFieldStream.StopAsync(token).ConfigureAwait(false);
        await WriteAuditBestEffortAsync("qhy-wide-stream-stop", new { gate, wideFieldStream.JobId,
            wideFieldStream.AnalyzedFrames, consumerFailure = wideFieldStream.AnalysisFailure?.GetType().Name,
            producerFailure = wideFieldStream.ProducerFailure?.Message,
            ownerChanged = false, budgetReset = false }).ConfigureAwait(false);
        if (gate.Disposition == GateDisposition.Passed) activeQhyJobs.TryRemove(wideFieldStream.JobId, out _);
        return gate;
    }

    private async Task AnalyzeWideFieldStreamFrameAsync(ObservationContext context, QhyJobSnapshot job,
        QhyFrameRecord frame, CancellationToken token)
    {
        if (!SameHash(await ComputeFileSha256Async(frame.FitsPath, token).ConfigureAwait(false), frame.Sha256))
            throw new PhysicalActionGateException(GateResult.Unknown("QHY_STREAM_RAW_CHANGED", "Immutable QHY stream frame hash changed."));
        var optical = await ReadLocalDualOpticalStateAsync(context, token).ConfigureAwait(false);
        if (optical is null || !SameHash(optical.ComputeSha256(), wideFieldOpticalSha ?? string.Empty))
            throw new QhyG3OptionalOpticalException("QHY continuous optical context is unavailable/changed; stream was retired, not silently rebound.");
        var samples = Array.Empty<G3FrameMountReadback>();
        lock (wideFieldStateGate) samples = wideFieldMountSamples.ToArray();
        var before = samples.LastOrDefault(s => s.ReportedUtc <= frame.ExposureStartedUtc);
        var after = samples.FirstOrDefault(s => s.ReportedUtc >= frame.ExposureEndedUtc);
        if (before is null || after is null) return; // Poll timestamps do not become fabricated shutter brackets.
        var bracket = samples.Where(s => s.ReportedUtc >= before.ReportedUtc && s.ReportedUtc <= after.ReportedUtc).ToArray();
        var maxSpan = configuration.G3.EffectiveFastSolvePair.MaximumMountSpanArcseconds;
        if (bracket.Any(s => s.CoordinateEpoch != before.CoordinateEpoch || s.PierSide != before.PierSide ||
            G3AcquisitionMotionPlanner.AngularSeparationArcseconds(before.RightAscensionDegrees, before.DeclinationDegrees,
                s.RightAscensionDegrees, s.DeclinationDegrees) > maxSpan)) return;
        var binding = CreateQhyAcceptedFrameMountBinding(context, job, frame, before, after);
        if (binding.Validate(context.Plan.ObservationRunId, configuration.ActionConfigurationSha256, commissioning!.Sha256,
            job.Id, frame.FrameId, frame.Sha256, maxSpan).Disposition != GateDisposition.Passed) return;
        var anchor = wideFieldFormalAnchor;
        var registration = anchor is null ? null : await Task.Run(() => QhyWideFieldRelativeRegistration.Register(anchor.Frame, frame, token), token).ConfigureAwait(false);
        var refresh = anchor is null || !registration!.Accepted ||
            frame.ExposureMidpointUtc - anchor.Frame.ExposureMidpointUtc > TimeSpan.FromSeconds(30);
        PlateSolveEvidence solve;
        if (refresh)
        {
            if (DateTimeOffset.UtcNow - wideFieldLastKeyframeAttemptUtc < TimeSpan.FromSeconds(10)) return;
            wideFieldLastKeyframeAttemptUtc = DateTimeOffset.UtcNow;
            try
            {
                solve = await RunWithG3PendingReturnTimeAsync("wide-qhy-keyframe", t => SolveExternalFitsAsync(frame.FitsPath,
                    frame.Settings.BitDepth, configuration.Qhy.FocalLengthMillimeters, configuration.Qhy.PixelSizeMicrometers,
                    frame.Settings.BinningX, anchor?.Solve.Result.Coordinates ?? TargetCoordinates(context.Plan),
                    "QHY/GS350 continuous formal keyframe", t), token).ConfigureAwait(false);
            }
            catch (WideFieldKeyframePreemptedException) { return; }
            if (!solve.Result.Success || solve.Result.Coordinates is null) return;
        }
        else
        {
            var transform = registration!.Transform!;
            var result = G3RegisteredWcs.RestoreCurrentResult(anchor!.Solve.Result, anchor.Solve.SourceWidthPixels,
                anchor.Solve.SourceHeightPixels, anchor.Solve.SolverIdentity,
                new(transform.A, transform.B, transform.X, transform.Y), frame.Metrics.SourceWidthPixels, frame.Metrics.SourceHeightPixels);
            var receipt = await PublishRunJsonEvidenceAsync("qhy-wide-relative-witness", "Independent-star relative wide-field measurement",
                new { reference = anchor.Frame.FitsPath, referenceSha256 = anchor.Frame.Sha256,
                    frame.FrameId, frame.Sha256, registration, binding, opticalStateSha256 = wideFieldOpticalSha,
                    formalCurrentFrameSolve = false, targetOrSlitMeasurement = false, mountMotionAuthority = false }, frame.FitsPath, token).ConfigureAwait(false);
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { [anchor.Frame.FitsPath] = anchor.Frame.Sha256, [anchor.Solve.EvidencePath] = anchor.Solve.EvidenceSha256,
              [frame.FitsPath] = frame.Sha256, [receipt] = await ComputeFileSha256Async(receipt, token).ConfigureAwait(false) };
            solve = new(result, anchor.Solve.Requested, double.NaN, anchor.Solve.SolverIdentity, frame.FitsPath,
                receipt, sources[receipt], frame.Metrics.SourceWidthPixels, frame.Metrics.SourceHeightPixels,
                frame.Settings.BinningX, DateTimeOffset.UtcNow)
            { Authority = "RegisteredWideFieldOpticalWitness", PredictionUncertaintyPixels = registration.CornerUncertaintyPixels,
                InferenceSources = sources, InferenceValidUntilUtc = frame.ExposureMidpointUtc.AddSeconds(45) };
        }
        var pose = new QhyG3PairQhySource(job, frame, solve, binding, QhyG3SolvePairSource.ReusedFreshQhySolve);
        if (refresh) wideFieldFormalAnchor = pose;
        lock (wideFieldStateGate)
        {
            wideFieldPoses.Add(pose);
            if (wideFieldPoses.Count > 16) wideFieldPoses.RemoveAt(0);
        }
    }

    private async Task<QhyG3PairQhySource?> TryGetWideFieldPairSourceAsync(ObservationContext context,
        G3PlateSolveProbeState probe, CancellationToken token, bool requireFormal = false, string? exceptFrameSha = null)
    {
        if (wideFieldStream?.AnalysisFailure is PhysicalActionGateException physical) throw physical;
        if (wideFieldStream?.ProducerFailure is PhysicalActionGateException producerPhysical) throw producerPhysical;
        var optical = await ReadLocalDualOpticalStateAsync(context, token).ConfigureAwait(false);
        if (optical is null || !SameHash(optical.ComputeSha256(), wideFieldOpticalSha ?? string.Empty) || probe.MountBinding is null) return null;
        QhyG3PairQhySource[] candidates;
        lock (wideFieldStateGate) candidates = wideFieldPoses.AsEnumerable().Reverse().ToArray();
        var policy = configuration.G3.EffectiveFastSolvePair;
        foreach (var source in candidates)
        {
            if (requireFormal && !source.Solve.IsFormal || SameHash(source.Frame.Sha256, exceptFrameSha ?? string.Empty) ||
                DateTimeOffset.UtcNow - source.Frame.ExposureMidpointUtc > policy.MaximumCachedQhyAge ||
                source.Frame.ExposureMidpointUtc > DateTimeOffset.UtcNow ||
                Math.Abs((source.Frame.ExposureMidpointUtc - G3ProbeMidpointUtc(probe)).TotalSeconds) > policy.MaximumPairMidpointSeparation.TotalSeconds ||
                source.Binding.AfterAcceptedFrame.PierSide != probe.MountBinding.PierSide ||
                source.Binding.AfterAcceptedFrame.CoordinateEpoch != probe.MountBinding.CoordinateEpoch ||
                G3AcquisitionMotionPlanner.AngularSeparationArcseconds(source.Binding.AfterAcceptedFrame.RightAscensionDegrees,
                    source.Binding.AfterAcceptedFrame.DeclinationDegrees, probe.MountBinding.RightAscensionDegrees,
                    probe.MountBinding.DeclinationDegrees) > policy.MaximumMountSpanArcseconds) continue;
            foreach (var item in source.Solve.InferenceSources.Concat(new Dictionary<string, string>
                { [source.Frame.FitsPath] = source.Frame.Sha256, [source.Solve.EvidencePath] = source.Solve.EvidenceSha256 }))
                if (!SameHash(await ComputeFileSha256Async(item.Key, token).ConfigureAwait(false), item.Value))
                    throw new PhysicalActionGateException(GateResult.Unknown("QHY_STREAM_PARENT_CHANGED", "A required immutable wide-field parent changed."));
            var lateOptical = await ReadLocalDualOpticalStateAsync(context, token).ConfigureAwait(false);
            if (lateOptical is null || !SameHash(lateOptical.ComputeSha256(), wideFieldOpticalSha ?? string.Empty) ||
                DateTimeOffset.UtcNow - source.Frame.ExposureMidpointUtc > policy.MaximumCachedQhyAge ||
                source.Solve.InferenceValidUntilUtc is { } expiry && DateTimeOffset.UtcNow >= expiry)
                continue;
            return source;
        }
        return null;
    }
}

internal sealed class WideFieldKeyframePreemptedException : OperationCanceledException { }
