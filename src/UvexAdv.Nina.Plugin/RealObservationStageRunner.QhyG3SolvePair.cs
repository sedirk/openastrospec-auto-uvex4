using System.Globalization;
using System.IO;
using System.Collections.Concurrent;
using NINA.Astrometry;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Opportunistic, same-pointing QHY/G3 solve-pair collection.  This partial
/// may request one QHY-service-owned exposure, but contains no mount command.
/// It produces a versioned candidate only; it never promotes the candidate to
/// wide-to-slit motion authority or substitutes it for G3PixelToMount.
/// </summary>
internal sealed partial class RealObservationStageRunner
{
    private readonly ConcurrentDictionary<string, byte> attemptedQhyG3SolvePairFrames = new(StringComparer.OrdinalIgnoreCase);
    private Guid? activeQhyG3FastPairJobId;

    private async Task TryCollectQhyG3FastSolvePairAsync(
        ObservationContext context,
        G3PlateSolveProbeState probe,
        CancellationToken cancellationToken)
    {
        var policy = configuration.G3.EffectiveFastSolvePair;
        if (!policy.Enabled)
        {
            qhyG3FastPairOutcome = "Disabled";
            return;
        }
        if (!attemptedQhyG3SolvePairFrames.TryAdd(probe.FramePath, 0)) return;
        latestQhyG3TransferCandidate = null;
        latestQhyG3TransferCandidateEvidencePath = null;
        qhyG3FastPairOutcome = "Attempting";

        var issues = policy.Validate();
        if (issues.Count > 0)
        {
            await RecordQhyG3PairSkipBestEffortAsync(
                context,
                probe,
                "QHY_G3_PAIR_POLICY_INVALID",
                string.Join(" ", issues),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(policy.MaximumPairWallClock);
        QhyG3PairQhySource? qhySource = null;
        Guid? quickJobId = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pairBudgetRemaining = policy.MaximumPairWallClock - (DateTimeOffset.UtcNow - G3ProbeStartUtc(probe));
            if (pairBudgetRemaining <= TimeSpan.Zero)
            {
                await RecordQhyG3PairSkipBestEffortAsync(
                    context,
                    probe,
                    "QHY_G3_PAIR_DEADLINE_ALREADY_EXCEEDED",
                    "The G3 exposure and solve already consumed the complete paired-WCS wall-clock budget; no QHY exposure was started.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            deadline.CancelAfter(pairBudgetRemaining < policy.MaximumPairWallClock
                ? pairBudgetRemaining
                : policy.MaximumPairWallClock);
            if (commissioning is null || nightSetup is null || commissioning.Value.Phd2SlitPlacement is null)
                throw new InvalidOperationException("Commissioning/Night Setup/installation-epoch bindings are unavailable for a QHY/G3 pair candidate.");
            if (probe.Solve?.Result.Success != true || probe.Solve.Result.Coordinates is null ||
                probe.MountBinding is null || probe.BeforeExposureMountReadback is null)
                throw new InvalidOperationException("The successful G3 solve lacks its immutable frame or dual-ended mount bracket.");

            qhySource = await TryPrepareCachedQhyPairSourceAsync(
                context,
                probe,
                policy,
                deadline.Token).ConfigureAwait(false);
            if (qhySource is null)
            {
                Report($"G3 已解算；立即拍摄一张 QHY {policy.QuickQhyExposureSeconds:G4}s 配对帧（不移动赤道仪）");
                var captured = await CaptureImmediateQhyPairSourceAsync(
                    context,
                    probe,
                    policy,
                    deadline.Token).ConfigureAwait(false);
                qhySource = captured.Source;
                quickJobId = captured.JobId;
            }
            else
            {
                Report("G3 已解算；复用同一 mount binding 下的新鲜 QHY WCS，零额外曝光生成双镜配对");
            }

            var finalMount = CaptureG3FrameMountReadback();
            var build = await BuildQhyG3PairCandidateAsync(
                context,
                probe,
                qhySource,
                finalMount,
                policy,
                deadline.Token).ConfigureAwait(false);
            if (build.Candidate is null || build.Gate.Disposition != GateDisposition.Passed)
            {
                await RecordQhyG3PairSkipBestEffortAsync(
                    context,
                    probe,
                    build.Gate.Code,
                    build.Gate.Message,
                    cancellationToken,
                    build.Gate.Metrics).ConfigureAwait(false);
                return;
            }

            latestQhyG3TransferCandidate = build.Candidate;
            qhyG3FastPairOutcome = "CandidateCreated";
            latestQhyG3TransferCandidateEvidencePath = await PublishRunJsonEvidenceAsync(
                "qhy-g3-fast-solve-pair",
                "Same-pointing QHY/G3 paired-WCS transfer candidate",
                new
                {
                    policy,
                    candidate = build.Candidate,
                    buildGate = new
                    {
                        disposition = build.Gate.Disposition.ToString(),
                        build.Gate.Code,
                        build.Gate.Message,
                        build.Gate.Metrics,
                    },
                    elapsedFromG3FrameCompletionToCandidateSeconds = Math.Max(
                        0,
                        (DateTimeOffset.UtcNow - probe.MountBinding.FrameCompletedUtc).TotalSeconds),
                    quickPairJobId = quickJobId,
                    mountMotionCommandCount = 0,
                    motionAuthority = false,
                    activationRequirement = "Aggregate and independently verify representative solve-pair samples, then import/activate a hash-bound QhyToG3Transfer record.",
                    prohibitedSubstitute = "This candidate is not G3PixelToMount and cannot authorize final slit-placement motion.",
                },
                probe.FramePath,
                cancellationToken,
                new Dictionary<string, string>
                {
                    ["candidateId"] = build.Candidate.CalibrationId,
                    ["candidateSha256"] = build.Candidate.CandidateSha256,
                    ["pairSource"] = build.Candidate.PairSource.ToString(),
                    ["motionAuthority"] = bool.FalseString,
                }).ConfigureAwait(false);
            context.Set("qhyG3FastPairOutcome", qhyG3FastPairOutcome);
            context.Set("qhyG3FastPairCandidateId", build.Candidate.CalibrationId);
            context.Set("qhyG3FastPairCandidateSha256", build.Candidate.CandidateSha256);
            context.Set("qhyG3FastPairEvidencePath", latestQhyG3TransferCandidateEvidencePath);
            await WriteAuditBestEffortAsync("qhy-g3-fast-solve-pair-created", new
            {
                build.Candidate.CalibrationId,
                build.Candidate.CandidateSha256,
                build.Candidate.PairSource,
                build.Candidate.PairMidpointSeparationSeconds,
                build.Candidate.PairWallClockSeconds,
                build.Candidate.MaximumObservedMountSpanArcseconds,
                build.Candidate.Model.G3MinusQhyEastArcseconds,
                build.Candidate.Model.G3MinusQhyNorthArcseconds,
                build.Candidate.Model.PredictedPrepositionMagnitudeArcseconds,
                build.Candidate.Model.PredictionUncertaintyArcseconds,
                evidencePath = latestQhyG3TransferCandidateEvidencePath,
                motionAuthority = false,
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if ((quickJobId ?? activeQhyG3FastPairJobId) is { } timedOutJob)
                await CancelQhyFastPairJobBestEffortAsync(timedOutJob).ConfigureAwait(false);
            await RecordQhyG3PairSkipBestEffortAsync(
                context,
                probe,
                "QHY_G3_PAIR_DEADLINE_EXCEEDED",
                $"The optional pair did not finish within {policy.MaximumPairWallClock.TotalSeconds:F1}s; the normal direct-G3 workflow continues.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if ((quickJobId ?? activeQhyG3FastPairJobId) is { } failedJob)
                await CancelQhyFastPairJobBestEffortAsync(failedJob).ConfigureAwait(false);
            await RecordQhyG3PairSkipBestEffortAsync(
                context,
                probe,
                "QHY_G3_PAIR_OPTIONAL_FAILURE",
                $"Optional fast solve-pair collection failed without mount motion: {ex.Message}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<QhyG3PairQhySource?> TryPrepareCachedQhyPairSourceAsync(
        ObservationContext context,
        G3PlateSolveProbeState probe,
        QhyG3FastPairPolicy policy,
        CancellationToken cancellationToken)
    {
        if (lastQhyAcquisition?.AcceptedFrameId is not { } acceptedFrameId || lastQhySolve is null ||
            lastQhyAcceptedFrameMountBinding is null || lastQhySolve.Result.Success != true ||
            lastQhySolve.Result.Coordinates is null)
            return null;
        var accepted = lastQhyAcquisition.Frames.SingleOrDefault(frame => frame.FrameId == acceptedFrameId);
        if (accepted is null) return null;
        var age = DateTimeOffset.UtcNow - accepted.ExposureMidpointUtc;
        if (age < TimeSpan.Zero || age > policy.MaximumCachedQhyAge) return null;
        var midpoint = G3ProbeMidpointUtc(probe);
        if (Math.Abs((accepted.ExposureMidpointUtc - midpoint).TotalSeconds) > policy.MaximumPairMidpointSeparation.TotalSeconds)
            return null;
        var gate = await ValidateQhyAcceptedFrameMountBindingForMotionAsync(
            context,
            lastQhyAcquisition,
            accepted,
            lastQhySolve,
            cancellationToken).ConfigureAwait(false);
        if (gate.Disposition != GateDisposition.Passed) return null;
        if (lastQhyAcceptedFrameMountBinding.Validate(
                context.Plan.ObservationRunId,
                configuration.ActionConfigurationSha256,
                commissioning?.Sha256 ?? string.Empty,
                lastQhyAcquisition.Id,
                accepted.FrameId,
                accepted.Sha256,
                policy.MaximumMountSpanArcseconds).Disposition != GateDisposition.Passed)
            return null;
        return new QhyG3PairQhySource(
            lastQhyAcquisition,
            accepted,
            lastQhySolve,
            lastQhyAcceptedFrameMountBinding,
            QhyG3SolvePairSource.ReusedFreshQhySolve);
    }

    private async Task<(QhyG3PairQhySource Source, Guid JobId)> CaptureImmediateQhyPairSourceAsync(
        ObservationContext context,
        G3PlateSolveProbeState probe,
        QhyG3FastPairPolicy policy,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var camera = await ConnectQhyAtCheckpointAsync(context, cancellationToken).ConfigureAwait(false);
        if (camera.Identity is null || !string.Equals(camera.Identity.StableId, context.Plan.ExpectedQhyCameraId, StringComparison.Ordinal))
            throw new InvalidOperationException("QHY identity changed before the immediate solve-pair exposure.");
        await CheckpointAndRejectStaleStageStackAsync(context, cancellationToken).ConfigureAwait(false);
        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        var before = CaptureG3FrameMountReadback();
        var attempt = Interlocked.Increment(ref qhyG3FastPairAttempt);
        var clientRequestId = $"{context.Plan.ObservationRunId}:qhy-g3-fast-pair:{attempt}";
        var request = new AcquisitionJobRequest(
            context.Plan.ObservationRunId,
            $"{context.Plan.Target.Name} / QHY-G3 fast solve pair",
            new[] { policy.QuickQhyExposureSeconds },
            configuration.Qhy.Gain,
            configuration.Qhy.Offset,
            MaximumAttempts: 1,
            BinningX: configuration.Qhy.Binning,
            BinningY: configuration.Qhy.Binning,
            ReadoutMode: configuration.Qhy.ReadoutMode,
            FilterName: configuration.Qhy.FilterName,
            TargetTemperatureC: configuration.Qhy.TargetTemperatureC,
            QualityThresholds: configuration.Qhy.QualityThresholds,
            RoiX: configuration.Qhy.RoiX,
            RoiY: configuration.Qhy.RoiY,
            RoiWidth: configuration.Qhy.RoiWidth,
            RoiHeight: configuration.Qhy.RoiHeight,
            ClientRequestId: clientRequestId,
            TargetRightAscensionDegrees: context.Plan.Target.RightAscensionDegrees,
            TargetDeclinationDegrees: context.Plan.Target.DeclinationDegrees,
            CoordinateEpoch: "ICRS",
            ControlLeaseSeconds: Math.Clamp((int)Math.Ceiling(policy.MaximumPairWallClock.TotalSeconds + 15), 30, 120));
        pendingQhyRequests[clientRequestId] = new PendingQhyRequest(
            context.Plan.ObservationRunId,
            QhyJobKind.Acquisition,
            clientRequestId,
            request);
        QhyJobSnapshot job;
        try
        {
            job = await qhy.StartOrAdoptAcquisitionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pendingQhyRequests.TryRemove(clientRequestId, out _);
        }
        RegisterActiveQhyJob(job);
        activeQhyG3FastPairJobId = job.Id;
        job = await qhy.WaitForQuiescentOrTerminalAsync(
            job.Id,
            snapshot => PublishQhyPreviewAsync(snapshot, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        ObserveQhySnapshot(job);
        var after = CaptureG3FrameMountReadback();
        if (job.State != QhyJobState.Completed || job.AcceptedFrameId is not { } frameId)
        {
            await CancelQhyFastPairJobBestEffortAsync(job.Id).ConfigureAwait(false);
            throw new InvalidOperationException(job.AttentionReason ?? job.Error ?? $"QHY fast-pair job ended in {job.State} without an accepted frame.");
        }
        activeQhyJobs.TryRemove(job.Id, out _);
        activeQhyG3FastPairJobId = null;
        var accepted = job.Frames.SingleOrDefault(frame => frame.FrameId == frameId)
            ?? throw new InvalidOperationException("QHY fast-pair AcceptedFrameId is absent from the immutable job frame list.");
        var binding = CreateQhyAcceptedFrameMountBinding(context, job, accepted, before, after);
        var bindingGate = binding.Validate(
            context.Plan.ObservationRunId,
            configuration.ActionConfigurationSha256,
            commissioning?.Sha256 ?? string.Empty,
            job.Id,
            accepted.FrameId,
            accepted.Sha256,
            policy.MaximumMountSpanArcseconds);
        if (bindingGate.Disposition != GateDisposition.Passed)
            throw new InvalidOperationException($"{bindingGate.Code}: {bindingGate.Message}");
        var requested = probe.Solve?.Result.Coordinates ?? TargetCoordinates(context.Plan);
        var solve = await SolveExternalFitsAsync(
            accepted.FitsPath,
            accepted.Settings.BitDepth,
            configuration.Qhy.FocalLengthMillimeters,
            configuration.Qhy.PixelSizeMicrometers,
            accepted.Settings.BinningX,
            requested,
            "QHY/GS350 immediate G3 solve-pair field",
            cancellationToken).ConfigureAwait(false);
        if (!solve.Result.Success || solve.Result.Coordinates is null)
            throw new InvalidOperationException("The single immediate QHY pairing frame did not plate-solve; no additional exposure tier is attempted.");
        return (new QhyG3PairQhySource(job, accepted, solve, binding, QhyG3SolvePairSource.ImmediateSingleQhyExposure), job.Id);
    }

    private async Task<QhyG3SolvePairBuildResult> BuildQhyG3PairCandidateAsync(
        ObservationContext context,
        G3PlateSolveProbeState probe,
        QhyG3PairQhySource qhySource,
        G3FrameMountReadback finalMount,
        QhyG3FastPairPolicy policy,
        CancellationToken cancellationToken)
    {
        if (commissioning is null || nightSetup is null || commissioning.Value.Phd2SlitPlacement is null ||
            probe.Image is null || probe.Solve?.Result.Coordinates is null || probe.MountBinding is null || probe.BeforeExposureMountReadback is null ||
            qhySource.Solve.Result.Coordinates is null)
            return new QhyG3SolvePairBuildResult(
                GateResult.Unknown("QHY_G3_PAIR_BINDING_MISSING", "A required solve, commissioning, Night Setup or mount binding disappeared before candidate construction."),
                null);

        var g3FrameSha = await ComputeFileSha256Async(probe.FramePath, cancellationToken).ConfigureAwait(false);
        var g3SolveSha = await ComputeFileSha256Async(probe.Solve.EvidencePath, cancellationToken).ConfigureAwait(false);
        var qhyFrameSha = await ComputeFileSha256Async(qhySource.Frame.FitsPath, cancellationToken).ConfigureAwait(false);
        var qhySolveSha = await ComputeFileSha256Async(qhySource.Solve.EvidencePath, cancellationToken).ConfigureAwait(false);
        if (!SameHash(g3FrameSha, probe.MountBinding.FrameSha256) ||
            !SameHash(g3SolveSha, probe.Solve.EvidenceSha256) ||
            !SameHash(qhyFrameSha, qhySource.Frame.Sha256) ||
            !SameHash(qhySolveSha, qhySource.Solve.EvidenceSha256))
            return new QhyG3SolvePairBuildResult(
                GateResult.Fail("QHY_G3_PAIR_SOURCE_HASH_CHANGED", "A paired FITS or WCS evidence file changed before the candidate could be written."),
                null);

        var qhyBindingGate = qhySource.Binding.Validate(
            context.Plan.ObservationRunId,
            configuration.ActionConfigurationSha256,
            commissioning.Sha256,
            qhySource.Job.Id,
            qhySource.Frame.FrameId,
            qhySource.Frame.Sha256,
            policy.MaximumMountSpanArcseconds);
        if (qhyBindingGate.Disposition != GateDisposition.Passed)
            return new QhyG3SolvePairBuildResult(qhyBindingGate, null);

        var g3ExposureMilliseconds = probe.Attempts
            .LastOrDefault(attempt => string.Equals(
                Path.GetFullPath(attempt.FramePath),
                Path.GetFullPath(probe.FramePath),
                StringComparison.OrdinalIgnoreCase))?.ExposureMilliseconds ?? 0;
        if (g3ExposureMilliseconds <= 0)
            return new QhyG3SolvePairBuildResult(
                GateResult.Unknown("QHY_G3_PAIR_G3_EXPOSURE_UNKNOWN", "The successful G3 frame is not bound to a versioned exposure-ladder tier."),
                null);
        var g3End = probe.MountBinding.FrameCompletedUtc;
        var g3Start = g3End - TimeSpan.FromMilliseconds(g3ExposureMilliseconds);
        var g3Midpoint = g3Start + TimeSpan.FromMilliseconds(g3ExposureMilliseconds / 2d);
        var qhyFrame = new QhyG3SolvedFrame(
            "QHY/GS350",
            context.Plan.ExpectedQhyCameraId,
            Path.GetFullPath(qhySource.Frame.FitsPath),
            qhySource.Frame.Sha256,
            Path.GetFullPath(qhySource.Solve.EvidencePath),
            qhySource.Solve.EvidenceSha256,
            qhySource.Frame.ExposureStartedUtc,
            qhySource.Frame.ExposureMidpointUtc,
            qhySource.Frame.ExposureEndedUtc,
            qhySource.Solve.CompletedUtc,
            "QHY service immutable manifest exposure timestamps",
            qhySource.Solve.SourceWidthPixels,
            qhySource.Solve.SourceHeightPixels,
            qhySource.Frame.Settings.BinningX,
            qhySource.Frame.Settings.BinningY,
            qhySource.Frame.Settings.RoiX,
            qhySource.Frame.Settings.RoiY,
            qhySource.Solve.SourceWidthPixels,
            qhySource.Solve.SourceHeightPixels,
            qhySource.Solve.Result.Coordinates.RADegrees,
            qhySource.Solve.Result.Coordinates.Dec,
            qhySource.Solve.Result.Pixscale,
            qhySource.Solve.Result.PositionAngle,
            qhySource.Solve.Result.Flipped,
            qhySource.Binding.BindingSha256);
        var g3Frame = new QhyG3SolvedFrame(
            "PHD2/G3/C11",
            configuration.Phd2.CameraStableId,
            Path.GetFullPath(probe.FramePath),
            probe.MountBinding.FrameSha256,
            Path.GetFullPath(probe.Solve.EvidencePath),
            probe.Solve.EvidenceSha256,
            g3Start,
            g3Midpoint,
            g3End,
            probe.Solve.CompletedUtc,
            "PHD2 save_image completion minus hash-locked requested exposure",
            probe.Image.Properties.Width,
            probe.Image.Properties.Height,
            configuration.G3.Binning,
            configuration.G3.Binning,
            0,
            0,
            probe.Image.Properties.Width,
            probe.Image.Properties.Height,
            probe.Solve.Result.Coordinates.RADegrees,
            probe.Solve.Result.Coordinates.Dec,
            probe.Solve.Result.Pixscale,
            probe.Solve.Result.PositionAngle,
            probe.Solve.Result.Flipped,
            probe.MountBinding.BindingSha256);
        var readbacks = new[]
        {
            PairReadback("g3-before-exposure", probe.BeforeExposureMountReadback),
            new QhyG3PairMountReadback(
                "g3-after-exposure",
                probe.MountBinding.RightAscensionDegrees,
                probe.MountBinding.DeclinationDegrees,
                probe.MountBinding.CoordinateEpoch,
                probe.MountBinding.PierSide,
                probe.MountBinding.MountReportedUtc),
            PairReadback("qhy-before-job", qhySource.Binding.BeforeJob),
            PairReadback("qhy-after-accepted-frame", qhySource.Binding.AfterAcceptedFrame),
            PairReadback("pair-final-readback", finalMount),
        };
        return QhyG3SolvePairBuilder.Build(new QhyG3SolvePairBuildRequest(
            policy,
            context.Plan.ObservationRunId,
            configuration.ActionConfigurationSha256,
            commissioning.Sha256,
            nightSetup.Value.NightSetupId,
            nightSetup.Sha256,
            commissioning.Value.Phd2SlitPlacement.InstallationEpochId,
            configuration.ExpectedTelescopeId,
            $"GS350/{context.Plan.ExpectedQhyCameraId}",
            $"C11/{configuration.Phd2.CameraStableId}",
            qhySource.Source,
            qhyFrame,
            g3Frame,
            readbacks,
            DateTimeOffset.UtcNow));
    }

    private static QhyG3PairMountReadback PairReadback(string role, G3FrameMountReadback value) => new(
        role,
        value.RightAscensionDegrees,
        value.DeclinationDegrees,
        value.CoordinateEpoch,
        value.PierSide,
        value.ReportedUtc);

    private static DateTimeOffset G3ProbeMidpointUtc(G3PlateSolveProbeState probe)
    {
        var start = G3ProbeStartUtc(probe);
        var exposure = probe.Attempts.LastOrDefault(attempt => string.Equals(
            Path.GetFullPath(attempt.FramePath),
            Path.GetFullPath(probe.FramePath),
            StringComparison.OrdinalIgnoreCase))?.ExposureMilliseconds ?? 0;
        return start + TimeSpan.FromMilliseconds(exposure / 2d);
    }

    private static DateTimeOffset G3ProbeStartUtc(G3PlateSolveProbeState probe)
    {
        var exposure = probe.Attempts.LastOrDefault(attempt => string.Equals(
            Path.GetFullPath(attempt.FramePath),
            Path.GetFullPath(probe.FramePath),
            StringComparison.OrdinalIgnoreCase))?.ExposureMilliseconds ?? 0;
        var end = probe.MountBinding?.FrameCompletedUtc ?? DateTimeOffset.MinValue;
        return end - TimeSpan.FromMilliseconds(exposure);
    }

    private async Task CancelQhyFastPairJobBestEffortAsync(Guid jobId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var current = await qhy.GetJobAsync(jobId, timeout.Token).ConfigureAwait(false);
            if (current is not null) ObserveQhySnapshot(current);
            if (current is not null && current.State is not (QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver) && qhy.HasOwnerSession(jobId))
            {
                var cancelling = await qhy.CancelAsync(jobId, timeout.Token).ConfigureAwait(false);
                ObserveQhySnapshot(cancelling);
                current = await qhy.WaitForCheckedTerminalAsync(
                    jobId,
                    TimeSpan.FromSeconds(10),
                    observed =>
                    {
                        ObserveQhySnapshot(observed);
                        return Task.CompletedTask;
                    },
                    timeout.Token).ConfigureAwait(false);
                ObserveQhySnapshot(current);
            }
            if (current?.State is QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver)
                activeQhyJobs.TryRemove(jobId, out _);
            if (activeQhyG3FastPairJobId == jobId) activeQhyG3FastPairJobId = null;
        }
        catch (Exception ex)
        {
            await WriteAuditBestEffortAsync("qhy-g3-fast-pair-stop-failed", new { jobId, ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task RecordQhyG3PairSkipBestEffortAsync(
        ObservationContext context,
        G3PlateSolveProbeState probe,
        string code,
        string reason,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, double>? metrics = null)
    {
        qhyG3FastPairOutcome = "Skipped";
        context.Set("qhyG3FastPairOutcome", qhyG3FastPairOutcome);
        context.Set("qhyG3FastPairSkipCode", code);
        try
        {
            latestQhyG3TransferCandidateEvidencePath = await PublishRunJsonEvidenceAsync(
                "qhy-g3-fast-solve-pair-skipped",
                "Optional same-pointing QHY/G3 solve-pair skipped",
                new
                {
                    policy = configuration.G3.EffectiveFastSolvePair,
                    code,
                    reason,
                    metrics,
                    g3Frame = probe.FramePath,
                    g3SolveEvidence = probe.Solve?.EvidencePath,
                    mountMotionCommandCount = 0,
                    directG3FallbackContinues = true,
                },
                string.IsNullOrWhiteSpace(probe.FramePath) ? null : probe.FramePath,
                cancellationToken).ConfigureAwait(false);
            context.Set("qhyG3FastPairEvidencePath", latestQhyG3TransferCandidateEvidencePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await WriteAuditBestEffortAsync("qhy-g3-fast-pair-skip-evidence-failed", new { code, reason, ex.Message }).ConfigureAwait(false);
        }
    }
}

internal sealed record QhyG3PairQhySource(
    QhyJobSnapshot Job,
    QhyFrameRecord Frame,
    PlateSolveEvidence Solve,
    QhyAcceptedFrameMountBinding Binding,
    QhyG3SolvePairSource Source);
