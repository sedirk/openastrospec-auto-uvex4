using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private static QhyG3SolvedFrame AsLocalDualFrame(ObservationContext context, QhyG3PairQhySource s) => new(
        "QHY/GS350", context.Plan.ExpectedQhyCameraId, s.Frame.FitsPath, s.Frame.Sha256,
        s.Solve.EvidencePath, s.Solve.EvidenceSha256, s.Frame.ExposureStartedUtc, s.Frame.ExposureMidpointUtc,
        s.Frame.ExposureEndedUtc, s.Solve.CompletedUtc, s.Solve.IsFormal ? "Native QHY formal keyframe" : "Registered wide-field optical witness; not a current-frame formal solve",
        s.Solve.SourceWidthPixels, s.Solve.SourceHeightPixels, s.Frame.Settings.BinningX, s.Frame.Settings.BinningY,
        s.Frame.Settings.RoiX, s.Frame.Settings.RoiY, s.Solve.SourceWidthPixels, s.Solve.SourceHeightPixels,
        s.Solve.Result.Coordinates.RADegrees, s.Solve.Result.Coordinates.Dec, s.Solve.Result.Pixscale,
        s.Solve.Result.PositionAngle, s.Solve.Result.Flipped, s.Binding.BindingSha256);
    private bool localDualModelDisabled;
    private int localDualQhyRefreshAttempts;
    private QhyG3PairQhySource? localDualDriftFirst;
    private QhyG3PairQhySource? localDualDriftSecond;
    private QhyG3LocalPrediction? lastLocalDualPrediction;
    private G3FieldMountBinding? lastLocalDualPredictionMount;
    private QhyG3LocalOpticalState? lastLocalDualOpticalState;
    private string? lastLocalDualOwnerContext;
    private readonly Dictionary<string, QhyG3LocalOpticalState> localDualInferenceOpticalStates = new(StringComparer.OrdinalIgnoreCase);

    private static string LocalDualArchiveRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVEX-ADV", "calibration", "qhy-g3");

    private async Task<QhyG3LocalOpticalState?> ReadLocalDualOpticalStateAsync(ObservationContext context, CancellationToken token)
    {
        if (nightSetup?.Value.FocusDomains?.SingleOrDefault(d => d.Role == FocusDomainRole.Gs350WideField) is not { } gs ||
            c11RunFocusLock.Position is not { } c11Position || !qhyAvailableForRun)
            return null;
        var c11 = ReadC11MainFocusOwner();
        var gate = c11RunFocusLock.Validate(c11, nightSetup.Value);
        if (gate.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(gate);
        var qhyStatus = await qhy.GetCameraAsync(token).ConfigureAwait(false);
        if (qhyStatus is not { Connected: true, Identity: not null } ||
            qhyStatus.Identity.StableId != context.Plan.ExpectedQhyCameraId || qhyStatus.TimestampUtc > DateTimeOffset.UtcNow ||
            DateTimeOffset.UtcNow - qhyStatus.TimestampUtc > TimeSpan.FromSeconds(10))
            return null;
        int? position;
        string authority;
        if (gs.Owner == FocusDomainConventions.Gs350NinaWorkerOwner)
        {
            if (qhyStatus.Focuser is not { Connected: true, PositionVerified: true, PositionSteps: not null } f ||
                f.DeviceId != gs.LogicalDeviceId || f.ObservationRunId != context.Plan.ObservationRunId ||
                f.TimestampUtc > DateTimeOffset.UtcNow || DateTimeOffset.UtcNow - f.TimestampUtc > TimeSpan.FromSeconds(10))
                return null;
            position = f.PositionSteps;
            authority = "NativeOwnerPosition";
        }
        else if (gs.Owner == FocusDomainConventions.Gs350Owner && gs.Limits.MaximumSingleMoveSteps == 0 && gs.Limits.MaximumCumulativeMoveSteps == 0)
        {
            // This is the explicit versioned manual-lock contract, not a fabricated live motor position.
            position = null;
            authority = "ExplicitManualLock";
        }
        else return null;
        var gsHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(gs, EvidenceJsonOptions)));
        var result = new QhyG3LocalOpticalState(c11.DeviceId, c11Position, gsHash, position, authority, configuration.G3.GainPercent);
        return result.IsComplete ? result : null;
    }

    private QhyG3LocalModelBinding CurrentLocalDualBinding(ObservationContext context, G3PlateSolveProbeState probe,
        QhyG3LocalOpticalState optical) => new(commissioning!.Sha256, nightSetup!.Value.NightSetupId, nightSetup.Sha256,
        commissioning.Value.Phd2SlitPlacement!.InstallationEpochId, configuration.ExpectedTelescopeId,
        context.Plan.ExpectedQhyCameraId, configuration.Phd2.CameraStableId,
        $"GS350/{context.Plan.ExpectedQhyCameraId}", $"C11/{configuration.Phd2.CameraStableId}",
        probe.MountBinding!.PierSide, probe.Image!.Properties.Width, probe.Image.Properties.Height, configuration.G3.Binning, optical)
        { ExpectedG3Flipped = configuration.G3.ExpectedWcsFlipped };

    private async Task PersistFocusBoundLocalPairAsync(ObservationContext context, QhyToG3TransferCandidate candidate, CancellationToken token)
    {
        var optical = await ReadLocalDualOpticalStateAsync(context, token).ConfigureAwait(false);
        if (optical is null)
        {
            await WriteAuditBestEffortAsync("qhy-g3-local-candidate-focus-unavailable", new {
                candidate.CalibrationId, ordinaryCandidateRetained = true, productionLocalModelEligible = false }).ConfigureAwait(false);
            return;
        }
        var receipt = await PublishRunJsonEvidenceAsync("qhy-g3-local-optical-state", "Focus-bound local paired-WCS source state",
            new { opticalState = optical, opticalStateSha256 = optical.ComputeSha256(), candidate.CandidateSha256,
                gs350ManualPositionIsNotLiveReadback = optical.Gs350PositionAuthority == "ExplicitManualLock",
                mountMotionCommandCount = 0, productionActivation = false }, candidate.G3.FramePath, token).ConfigureAwait(false);
        var path = await QhyG3LocalModelArchive.SaveAsync(LocalDualArchiveRoot, candidate, optical, receipt,
            await ComputeFileSha256Async(receipt, token).ConfigureAwait(false), token).ConfigureAwait(false);
        context.Set("qhyG3FocusBoundLocalCandidatePath", path);
    }

    private async Task<GateResult> ValidateLocalDualOpticalStateForInferenceAsync(ObservationContext context,
        PlateSolveEvidence solve, CancellationToken token)
    {
        if (localDualModelDisabled || !localDualInferenceOpticalStates.TryGetValue(solve.EvidenceSha256, out var expected))
            return GateResult.Unknown("QHY_G3_LOCAL_MODEL_RETIRED", "The local model was retired; no old predicted coordinates authorize motion.");
        var current = await ReadLocalDualOpticalStateAsync(context, token).ConfigureAwait(false);
        return current is not null && SameHash(current.ComputeSha256(), expected.ComputeSha256())
            ? GateResult.Pass("QHY_G3_LOCAL_OPTICAL_STATE_UNCHANGED", "Current C11/GS350 focus and gain remain bound to the local prediction.")
            : GateResult.Unknown("QHY_G3_LOCAL_OPTICAL_STATE_CHANGED", "Current optical state no longer matches the immutable local prediction.");
    }

    private async Task<PlateSolveEvidence?> TryLocalDualSparseSolveAsync(ObservationContext context,
        G3PlateSolveProbeState probe, CancellationToken token)
    {
        if (localDualModelDisabled || probe.Image is null || probe.MountBinding is null || probe.Solve is null ||
            commissioning?.Value.Phd2SlitPlacement is not { } placement || nightSetup is null ||
            !configuration.G3.EffectiveFastSolvePair.Enabled || !qhyAvailableForRun)
            return null;
        await ValidateSparseBoundaryAsync(context, probe, token).ConfigureAwait(false);
        var optical = await ReadLocalDualOpticalStateAsync(context, token).ConfigureAwait(false);
        if (optical is null) return null;
        var binding = CurrentLocalDualBinding(context, probe, optical);
        var archive = await QhyG3LocalModelArchive.LoadAsync(LocalDualArchiveRoot, binding, DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(30), token).ConfigureAwait(false);
        if (archive.IntegrityFailure || archive.Samples.Count == 0)
        {
            await PublishRunJsonEvidenceAsync("qhy-g3-local-model-unavailable", "Local model not activated: source validation or sample count",
                new { archive.RejectedRecords, archive.IntegrityFailure, sampleCount = archive.Samples.Count,
                    minimumIndependentSamples = 3, oldSchema1NotAutoPromoted = true, directSearchAndMosaicRemainAvailable = true },
                probe.FramePath, token).ConfigureAwait(false);
            return null;
        }
        var policy = configuration.G3.EffectiveFastSolvePair;
        var maxUncertainty = placement.TargetSearchRadiusPixels *
            archive.Samples.Min(s => s.Candidate.G3.PixelScaleArcseconds);
        var currentStream = await TryGetWideFieldPairSourceAsync(context, probe, token).ConfigureAwait(false);
        var localSamples = currentStream is null ? archive.Samples : QhyG3LocalModelPolicy.SelectLocalSamples(
            archive.Samples, AsLocalDualFrame(context, currentStream), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30));
        if (localSamples.Count < 3 || !QhyG3LocalModelPolicy.HasIndependentFitSupport(localSamples, binding, maxUncertainty))
        {
            await WriteAuditBestEffortAsync("qhy-g3-local-model-fit-preflight-rejected", new {
                extraQhyExposureStarted = false, independentHoldoutOrGeometryInsufficient = true }).ConfigureAwait(false);
            return null;
        }
        bool CacheApplies(QhyG3PairQhySource? s) => s is not null &&
            s.Binding.AfterAcceptedFrame.CoordinateEpoch == probe.MountBinding.CoordinateEpoch &&
            s.Binding.AfterAcceptedFrame.PierSide == probe.MountBinding.PierSide &&
            DateTimeOffset.UtcNow - s.Frame.ExposureMidpointUtc <= policy.MaximumCachedQhyAge &&
            G3AcquisitionMotionPlanner.AngularSeparationArcseconds(s.Binding.AfterAcceptedFrame.RightAscensionDegrees,
                s.Binding.AfterAcceptedFrame.DeclinationDegrees, probe.MountBinding.RightAscensionDegrees,
                probe.MountBinding.DeclinationDegrees) <= policy.MaximumMountSpanArcseconds;
        if (!CacheApplies(localDualDriftFirst) || !CacheApplies(localDualDriftSecond))
        {
            // Expired/old-pointing optional cache is not a changed live owner.
            // Retiring it does not reset the two-exposure allowance.
            localDualDriftFirst = null; localDualDriftSecond = null;
        }
        var source = await TryGetWideFieldPairSourceAsync(context, probe, token, exceptFrameSha: currentStream?.Frame.Sha256).ConfigureAwait(false)
            ?? localDualDriftFirst ?? await TryPrepareCachedQhyPairSourceAsync(context, probe, policy, token).ConfigureAwait(false)
            ?? await CaptureLocalDualWitnessAsync(context, probe, policy, token).ConfigureAwait(false);
        if (source is null) return null;
        localDualDriftFirst = source;
        var second = currentStream ?? localDualDriftSecond ?? await CaptureLocalDualWitnessAsync(context, probe, policy, token).ConfigureAwait(false);
        if (second is null) return null;
        localDualDriftSecond = second;
        await ValidateSparseBoundaryAsync(context, probe, token).ConfigureAwait(false);
        var afterOptical = await ReadLocalDualOpticalStateAsync(context, token).ConfigureAwait(false);
        if (afterOptical is null || !SameHash(optical.ComputeSha256(), afterOptical.ComputeSha256()))
            throw new PhysicalActionGateException(GateResult.Unknown("QHY_G3_LOCAL_FOCUS_CHANGED", "Focus state changed while preparing the local model."));
        var current = CaptureG3FrameMountReadback();
        foreach (var witness in new[] { source, second })
        {
        var sourceGate = witness.Binding.Validate(context.Plan.ObservationRunId, configuration.ActionConfigurationSha256,
            commissioning.Sha256, witness.Job.Id, witness.Frame.FrameId, witness.Frame.Sha256, policy.MaximumMountSpanArcseconds);
        if (sourceGate.Disposition != GateDisposition.Passed) throw new PhysicalActionGateException(sourceGate);
        if (current.CoordinateEpoch != witness.Binding.AfterAcceptedFrame.CoordinateEpoch ||
            current.PierSide != witness.Binding.AfterAcceptedFrame.PierSide ||
            G3AcquisitionMotionPlanner.AngularSeparationArcseconds(current.RightAscensionDegrees, current.DeclinationDegrees,
                witness.Binding.AfterAcceptedFrame.RightAscensionDegrees, witness.Binding.AfterAcceptedFrame.DeclinationDegrees) > policy.MaximumMountSpanArcseconds)
            throw new PhysicalActionGateException(GateResult.Unknown("QHY_G3_LOCAL_QHY_BINDING_STALE", "Current QHY witness is no longer at the stationary G3 pointing."));
        if (!SameHash(await ComputeFileSha256Async(witness.Frame.FitsPath, token).ConfigureAwait(false), witness.Frame.Sha256) ||
            !SameHash(await ComputeFileSha256Async(witness.Solve.EvidencePath, token).ConfigureAwait(false), witness.Solve.EvidenceSha256))
            throw new PhysicalActionGateException(GateResult.Unknown("QHY_G3_LOCAL_CURRENT_SOURCE_CHANGED", "The current QHY witness or formal solve changed."));
        }
        var q = AsLocalDualFrame(context, second);
        var firstQhy = AsLocalDualFrame(context, source);
        var actualExposure = probe.ExposureMillisecondsActual;
        if (!QhyG3LocalModelPolicy.CurrentExposuresAligned(probe.ExposureEndedUtc, actualExposure,
            q, policy, DateTimeOffset.UtcNow, out var alignment))
        {
            await PublishRunJsonEvidenceAsync("qhy-g3-local-time-alignment-rejected", "Local dual model needs near-simultaneous current pixels",
                new { alignment, actualG3ExposureMilliseconds = actualExposure,
                    g3CompletedUtc = probe.ExposureEndedUtc, qhyMidpointUtc = q.ExposureMidpointUtc,
                    policy.MaximumPairMidpointSeparation, policy.MaximumPairWallClock,
                    mountStableIsNotPixelStable = true, fallbackContinues = true }, probe.FramePath, token).ConfigureAwait(false);
            return null;
        }
        localSamples = QhyG3LocalModelPolicy.SelectLocalSamples(archive.Samples, q, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30));
        var model = QhyG3LocalModelPolicy.Evaluate(new(binding, q, localSamples,
            new(TimeSpan.FromMinutes(30), policy.MaximumCachedQhyAge, 5, maxUncertainty), DateTimeOffset.UtcNow));
        await PublishRunJsonEvidenceAsync("qhy-g3-local-model-decision", "Causal independently validated local dual-optics prediction",
            new { model, archive.RejectedRecords, opticalState = optical, currentQhy = q, schema1SamplesUsed = false,
                originalReturnClock = true, budgetReset = false, fineMotionAuthority = false }, probe.FramePath, token).ConfigureAwait(false);
        if (model.Prediction is not { } p) return null;
        var drift = QhyG3SkyDriftPolicy.Build(firstQhy, q,
            Math.Sqrt(Math.Pow(firstQhy.PixelScaleArcseconds, 2) + Math.Pow(source.Solve.PredictionUncertaintyPixels * firstQhy.PixelScaleArcseconds, 2) + Math.Pow(policy.MaximumMountSpanArcseconds, 2)),
            Math.Sqrt(Math.Pow(q.PixelScaleArcseconds, 2) + Math.Pow(second.Solve.PredictionUncertaintyPixels * q.PixelScaleArcseconds, 2) + Math.Pow(policy.MaximumMountSpanArcseconds, 2)),
            probe.ExposureStartedUtc, probe.ExposureEndedUtc, actualExposure, policy, DateTimeOffset.UtcNow,
            maxUncertainty - p.PredictionUncertaintyArcseconds);
        await PublishRunJsonEvidenceAsync("qhy-g3-local-sky-drift", "Measured wide-field optical sky drift and latency uncertainty",
            new { drift, firstQhy, secondQhy = q, nativeG3ExposureStartedUtc = probe.ExposureStartedUtc,
                nativeG3ExposureEndedUtc = probe.ExposureEndedUtc, actualExposure,
                mountReadbackDriftWasNotAssumedZero = true, g3StarsNeededForDriftWitness = false,
                nativeFormalFitCovarianceAvailable = false,
                differentialErrorAuthority = "Empirical local paired-WCS validation plus one-QHY-pixel and mount-bracket RSS floor; not a measured native solver covariance or absolute encoder accuracy" },
            probe.FramePath, token).ConfigureAwait(false);
        if (drift.Witness is not { } sky) return null;
        var centerAtG3Midpoint = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(sky.G3MidpointQhyRightAscensionDegrees,
            sky.G3MidpointQhyDeclinationDegrees, p.G3MinusQhyEastArcseconds, p.G3MinusQhyNorthArcseconds);
        p = p with { RightAscensionDegrees = centerAtG3Midpoint.RaDegrees, DeclinationDegrees = centerAtG3Midpoint.DecDegrees,
            PredictionUncertaintyArcseconds = p.PredictionUncertaintyArcseconds + sky.AdditionalUncertaintyArcseconds };
        await ValidateSparseBoundaryAsync(context, probe, token).ConfigureAwait(false);
        var currentNode = sparseG3Nodes.LastOrDefault(n => n.Path == probe.FramePath);
        if (currentNode is null) return null;
        var result = new PlateSolveResult { Success = true, Coordinates = new Coordinates(p.RightAscensionDegrees,
            p.DeclinationDegrees, Epoch.J2000, Coordinates.RAType.Degrees), Pixscale = p.PixelScaleArcseconds,
            PositionAngle = p.PositionAngleDegrees, Flipped = p.Flipped };
        var parents = new Dictionary<string, string>(archive.SourceFiles, StringComparer.OrdinalIgnoreCase)
        { [source.Frame.FitsPath] = source.Frame.Sha256, [source.Solve.EvidencePath] = source.Solve.EvidenceSha256,
          [second.Frame.FitsPath] = second.Frame.Sha256, [second.Solve.EvidencePath] = second.Solve.EvidenceSha256 };
        foreach (var pair in source.Solve.InferenceSources.Concat(second.Solve.InferenceSources)) parents[pair.Key] = pair.Value;
        var inferred = await CreateSparseInferenceAsync(context, currentNode, probe.Solve, result,
            probe.Solve.SolverIdentity,
            "LocalDualOpticsPrediction", p.PredictionUncertaintyArcseconds / p.PixelScaleArcseconds,
            new { prediction = p, currentQhy = q, skyDrift = sky, opticalState = optical, sourceCandidateHashes = archive.Samples.Select(s => s.Candidate.CandidateSha256) }, token,
            additionalSources: parents).ConfigureAwait(false);
        if (inferred is not null)
        {
            if (DateTimeOffset.UtcNow >= sky.ValidUntilUtc) return null;
            var finalExpiry = inferred.InferenceValidUntilUtc is { } existing && existing < sky.ValidUntilUtc ? existing : sky.ValidUntilUtc;
            if (DateTimeOffset.UtcNow >= finalExpiry) return null;
            inferred = inferred with { InferenceValidUntilUtc = finalExpiry };
            localDualInferenceOpticalStates[inferred.EvidenceSha256] = optical;
            lastLocalDualPrediction = p; lastLocalDualPredictionMount = probe.MountBinding;
            lastLocalDualOpticalState = optical; lastLocalDualOwnerContext = sparseG3Context;
        }
        return inferred;
    }

    private Task<QhyG3PairQhySource?> CaptureLocalDualWitnessAsync(ObservationContext context,
        G3PlateSolveProbeState probe, QhyG3FastPairPolicy policy, CancellationToken token) =>
        G3SparseRecoveryWorkflow.WithoutContinuousOwnerAsync(wideFieldStream is not null,
            t => CaptureLocalDualWitnessCoreAsync(context, probe, policy, t), token);

    private async Task<QhyG3PairQhySource?> CaptureLocalDualWitnessCoreAsync(ObservationContext context,
        G3PlateSolveProbeState probe, QhyG3FastPairPolicy policy, CancellationToken token)
    {
        // The continuous job already owns the camera. A missing contemporaneous
        // pose is an optional inference miss, not permission to start another job
        // (nor a reason to throw away this perfectly usable G3 exposure).
        if (localDualQhyRefreshAttempts >= 2) return null;
        // Precharge before native dispatch; same-run retry cannot gain more exposures.
        var ordinal = ++localDualQhyRefreshAttempts;
        await PublishRunJsonEvidenceAsync("qhy-g3-local-current-qhy-refresh", "Bounded stationary optical drift witness",
            new { ordinal, maximumAttempts = 2, policy.QuickQhyExposureSeconds, originalReturnClock = true,
                noMountMovement = true, budgetReset = false }, probe.FramePath, token).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(policy.MaximumPairWallClock);
            var captured = await RunWithG3PendingReturnTimeAsync("local-dual-qhy-refresh", t =>
                CaptureImmediateQhyPairSourceAsync(context, probe, policy, t), deadline.Token).ConfigureAwait(false);
            return captured.Source;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (PhysicalActionGateException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (activeQhyG3FastPairJobId is { } known)
            {
                await CancelQhyFastPairJobBestEffortAsync(known).ConfigureAwait(false);
                using var stopRead = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var stopped = await qhy.GetJobAsync(known, stopRead.Token).ConfigureAwait(false);
                if (stopped?.State is not (UvexAdv.Qhy.Core.QhyJobState.Completed or UvexAdv.Qhy.Core.QhyJobState.Cancelled or
                    UvexAdv.Qhy.Core.QhyJobState.Faulted or UvexAdv.Qhy.Core.QhyJobState.TakenOver) || stopped.CompletedUtc is null)
                    throw new PhysicalActionGateException(GateResult.Unknown("QHY_G3_LOCAL_REFRESH_STOP_UNCONFIRMED",
                        "Optional optical witness did not confirm its owned terminal boundary."));
            }
            else if (ex is not QhyG3OptionalOpticalException)
                throw new PhysicalActionGateException(GateResult.Unknown("QHY_G3_LOCAL_REFRESH_STOP_UNCONFIRMED",
                    "Failed optical witness has no confirmed owned terminal/identity receipt."));
            await WriteAuditBestEffortAsync("qhy-g3-local-qhy-refresh-unavailable", new {
                reason = ex.Message, ordinal, originalFailureRetained = true, originalBudgetRetained = true }).ConfigureAwait(false);
            return null;
        }
    }

    private async Task CheckLocalDualModelAgainstFormalAsync(PlateSolveEvidence formal, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (lastLocalDualPrediction is not { } p || lastLocalDualPredictionMount is not { } origin || !formal.IsFormal || !formal.Result.Success)
            return;
        var current = CaptureG3FrameMountReadback();
        var c11 = ReadC11MainFocusOwner();
        if (sparseG3Context != lastLocalDualOwnerContext || current.PierSide != origin.PierSide || current.CoordinateEpoch != origin.CoordinateEpoch ||
            lastLocalDualOpticalState?.C11PositionSteps != c11.PositionSteps || lastLocalDualOpticalState.C11FocuserDeviceId != c11.DeviceId)
        {
            localDualModelDisabled = true;
            return;
        }
        var delta = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(origin.RightAscensionDegrees, origin.DeclinationDegrees,
            current.RightAscensionDegrees, current.DeclinationDegrees);
        var predicted = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(p.RightAscensionDegrees, p.DeclinationDegrees, delta.RaArcseconds, delta.DecArcseconds);
        var separation = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(predicted.RaDegrees, predicted.DecDegrees,
            formal.Result.Coordinates.RADegrees, formal.Result.Coordinates.Dec);
        var cornerPixels = Math.Sqrt(Math.Pow(formal.SourceWidthPixels, 2) + Math.Pow(formal.SourceHeightPixels, 2)) / 2;
        var geometryError = cornerPixels * (Math.Abs(formal.Result.Pixscale - p.PixelScaleArcseconds) +
            2 * formal.Result.Pixscale * Math.Abs(Math.Sin(Math.IEEERemainder(formal.Result.PositionAngle - p.PositionAngleDegrees, 360) * Math.PI / 360)));
        var allowed = p.PredictionUncertaintyArcseconds + MountCommandArrivalToleranceArcseconds + 3 * formal.Result.Pixscale;
        var disagrees = !double.IsFinite(separation + geometryError) || formal.Result.Flipped != p.Flipped || separation + geometryError > allowed;
        if (disagrees) localDualModelDisabled = true;
        await WriteAuditBestEffortAsync("qhy-g3-local-model-formal-crosscheck", new { p.ModelSha256,
            formalSolve = formal.EvidencePath, separationArcseconds = separation, cornerGeometryErrorArcseconds = geometryError,
            allowedArcseconds = allowed, modelDisabledForRunner = localDualModelDisabled,
            directFormalAcquisitionRetained = true, originalBudgetRetained = true }).ConfigureAwait(false);
    }
}
