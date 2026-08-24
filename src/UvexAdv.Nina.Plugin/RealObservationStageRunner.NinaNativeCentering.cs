using System.Globalization;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Model;
using NINA.PlateSolving;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private async Task<StageResult> CoarseCenterWithNinaNativeAsync(
        ObservationContext context,
        QhyCoarseCenteringLimits limits,
        CancellationToken cancellationToken)
    {
        var previousWorstCaseDuration = context.RemainingWorstCaseDuration;
        context.RemainingWorstCaseDuration =
            (previousWorstCaseDuration ?? context.Plan.PlannedDuration) + limits.MaximumElapsedTime;

        var target = TargetCoordinates(context.Plan);
        var initialSolve = lastQhySolve!;
        var initialAcquisition = lastQhyAcquisition!;
        if (initialAcquisition.AcceptedFrameId is not { } initialFrameId ||
            initialAcquisition.Frames.SingleOrDefault(frame => frame.FrameId == initialFrameId) is not { } initialFrame)
        {
            context.RemainingWorstCaseDuration = previousWorstCaseDuration;
            return Attention(
                ObservationStage.CoarseCenter,
                "QHY_NATIVE_CENTER_SOURCE_FRAME_MISSING",
                "N.I.N.A. native centering requires the accepted immutable QHY frame that produced the current WCS.");
        }

        var initialBindingGate = await ValidateQhyAcceptedFrameMountBindingForMotionAsync(
            context,
            initialAcquisition,
            initialFrame,
            initialSolve,
            cancellationToken).ConfigureAwait(false);
        if (initialBindingGate.Disposition != GateDisposition.Passed)
        {
            context.RemainingWorstCaseDuration = previousWorstCaseDuration;
            lastQhySolve = null;
            lastQhySolveMountBinding = null;
            lastQhyAcceptedFrameMountBinding = null;
            return new StageResult(initialBindingGate, initialFrame.FitsPath);
        }

        var origin = telescopeMediator.GetCurrentPosition();
        var originPierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
        var mountGate = ValidateQhyCoarseMountState(originPierSide);
        if (mountGate.Disposition != GateDisposition.Passed)
        {
            context.RemainingWorstCaseDuration = previousWorstCaseDuration;
            return new StageResult(mountGate, initialSolve.SourcePath);
        }

        qhyCoarseStartedUtc ??= DateTimeOffset.UtcNow;
        var state = new QhyPendingCoarseReturn(
            origin,
            originPierSide,
            CurrentRaTangentOffsetArcseconds: 0,
            CurrentDeclinationOffsetArcseconds: 0,
            qhyCoarseCumulativeArcseconds,
            qhyCoarseStartedUtc.Value,
            DeclaredEvidencePath: string.Empty);
        var initialResidual = AngularSeparationArcseconds(target, initialSolve.Result.Coordinates);
        var declaredPath = await PublishRunJsonEvidenceAsync(
            "qhy-nina-native-centering-declared",
            "N.I.N.A. native centering with QHY-service capture and guarded mount authority",
            new
            {
                engine = "NINA.PlateSolving.CenteringSolver",
                ninaAssemblyVersion = typeof(CenteringSolver).Assembly.GetName().Version?.ToString(),
                captureOwner = "UvexAdv.Qhy.Service",
                mountOwner = "N.I.N.A.",
                syncPolicy = "NoSync=true",
                fullNativeCorrectionsAreSegmentedByTheCommissionedEnvelope = true,
                schemaVersion = limits.SchemaVersion,
                limits.MaximumSingleCorrectionArcseconds,
                limits.MaximumCumulativeCorrectionArcseconds,
                limits.MaximumCorrectionAttempts,
                maximumElapsedSeconds = limits.MaximumElapsedTime.TotalSeconds,
                configuration.Qhy.CenteringToleranceArcseconds,
                initialResidualArcseconds = initialResidual,
                initialSolve = new
                {
                    initialSolve.SolverIdentity,
                    initialSolve.SourcePath,
                    solvedRaDegrees = initialSolve.Result.Coordinates.RADegrees,
                    solvedDecDegrees = initialSolve.Result.Coordinates.Dec,
                },
                target = new { raDegrees = target.RADegrees, decDegrees = target.Dec },
                origin = new { raDegrees = origin.RADegrees, decDegrees = origin.Dec, pierSide = originPierSide },
            },
            initialSolve.SourcePath,
            cancellationToken).ConfigureAwait(false);
        state = state with { DeclaredEvidencePath = declaredPath };

        var captureNumber = 0;
        var priorResidual = initialResidual;
        NinaNativeCenteringException? deferredFailure = null;

        async Task<PlateSolveResult> CaptureAndSolveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref captureNumber) == 1)
            {
                return initialSolve.Result;
            }

            qhyAcquisitionJobId = null;
            qhyAcquisitionMountReadbackJobId = null;
            qhyAcquisitionBeforeJobMountReadback = null;
            lastQhyAcquisition = null;
            lastQhySolve = null;
            lastQhySolveMountBinding = null;
            lastQhyAcceptedFrameMountBinding = null;
            qhyAcquisitionAttempt++;

            var reacquired = await AcquireQhyWideFieldAsync(context, ct).ConfigureAwait(false);
            if (!reacquired.CanAdvance || lastQhySolve is null || lastQhyAcquisition is null)
            {
                deferredFailure = new NinaNativeCenteringException(
                    "QHY_NATIVE_CENTER_REACQUIRE_FAILED",
                    $"N.I.N.A. requested a fresh capture after a correction, but QHY reacquisition did not pass: {reacquired.Gate.Code}: {reacquired.Gate.Message}",
                    reacquired.EvidencePath);
                throw deferredFailure;
            }

            var freshResidual = AngularSeparationArcseconds(target, lastQhySolve.Result.Coordinates);
            if (freshResidual > configuration.Qhy.CenteringToleranceArcseconds &&
                freshResidual > priorResidual * 1.25)
            {
                deferredFailure = new NinaNativeCenteringException(
                    "QHY_NATIVE_CENTER_RESPONSE_INVALID",
                    $"A N.I.N.A. native centering correction worsened the fresh QHY WCS residual from {priorResidual:F1} to {freshResidual:F1} arcsec.",
                    lastQhySolve.EvidencePath);
                throw deferredFailure;
            }
            priorResidual = freshResidual;
            return lastQhySolve.Result;
        }

        async Task<bool> GuardedSlewAsync(
            Coordinates ninaRequestedCoordinate,
            CancellationToken ct,
            Func<Task<bool>> unusedNativeDispatch)
        {
            _ = unusedNativeDispatch;
            if (lastQhySolve is null || lastQhyAcquisition?.AcceptedFrameId is not { } sourceFrameId ||
                lastQhyAcquisition.Frames.SingleOrDefault(frame => frame.FrameId == sourceFrameId) is not { } sourceFrame)
            {
                throw new NinaNativeCenteringException(
                    "QHY_NATIVE_CENTER_FRESH_SOURCE_REQUIRED",
                    "N.I.N.A. requested a correction without a fresh accepted QHY frame and WCS.",
                    state.DeclaredEvidencePath);
            }

            await RequireImmediatePhysicalActionGatesAsync(context, ct).ConfigureAwait(false);
            var currentMountGate = ValidateQhyCoarseMountState(originPierSide);
            if (currentMountGate.Disposition != GateDisposition.Passed)
            {
                throw new NinaNativeCenteringException(currentMountGate.Code, currentMountGate.Message, lastQhySolve.SourcePath);
            }
            var sourceBindingGate = await ValidateQhyAcceptedFrameMountBindingForMotionAsync(
                context,
                lastQhyAcquisition,
                sourceFrame,
                lastQhySolve,
                ct).ConfigureAwait(false);
            if (sourceBindingGate.Disposition != GateDisposition.Passed)
            {
                throw new NinaNativeCenteringException(sourceBindingGate.Code, sourceBindingGate.Message, lastQhySolve.SourcePath);
            }

            var current = telescopeMediator.GetCurrentPosition();
            EnsureFiniteReportedCoordinates(current);
            var (fullRaArcseconds, fullDecArcseconds) = SignedTangentOffsetArcseconds(current, ninaRequestedCoordinate);
            var fullMagnitude = Math.Sqrt(fullRaArcseconds * fullRaArcseconds + fullDecArcseconds * fullDecArcseconds);
            if (!double.IsFinite(fullMagnitude) || fullMagnitude <= 0)
            {
                throw new NinaNativeCenteringException(
                    "QHY_NATIVE_CENTER_CORRECTION_INVALID",
                    "N.I.N.A. native centering requested a non-finite or zero correction.",
                    lastQhySolve.SourcePath);
            }

            var moveMagnitude = Math.Min(fullMagnitude, limits.MaximumSingleCorrectionArcseconds);
            var scale = moveMagnitude / fullMagnitude;
            var raArcseconds = fullRaArcseconds * scale;
            var decArcseconds = fullDecArcseconds * scale;
            var boundedCoordinate = ApplySkyCorrection(current, raArcseconds, decArcseconds);
            var (nextOriginRa, nextOriginDec) = SignedTangentOffsetArcseconds(origin, boundedCoordinate);
            var nextRadius = Math.Sqrt(nextOriginRa * nextOriginRa + nextOriginDec * nextOriginDec);
            var bounded = ValidateQhyCoarseMoveAndReturnReserve(state, moveMagnitude, nextRadius);
            if (bounded.Disposition != GateDisposition.Passed)
            {
                throw new NinaNativeCenteringException(bounded.Code, bounded.Message, lastQhySolve.SourcePath);
            }
            var horizon = ValidateCommandCoordinateHorizon(context, boundedCoordinate, "N.I.N.A. native QHY centering segment");
            if (horizon.Disposition != GateDisposition.Passed)
            {
                throw new NinaNativeCenteringException(horizon.Code, horizon.Message, lastQhySolve.SourcePath);
            }

            var intentPath = await PublishRunJsonEvidenceAsync(
                "qhy-nina-native-centering-move-intent",
                $"Guarded N.I.N.A. native centering segment {qhyCoarseCorrectionAttempts + 1}",
                new
                {
                    engine = "NINA.PlateSolving.CenteringSolver",
                    ninaRequestedRaDegrees = ninaRequestedCoordinate.RADegrees,
                    ninaRequestedDecDegrees = ninaRequestedCoordinate.Dec,
                    ninaRequestedFullCorrectionArcseconds = fullMagnitude,
                    segmented = fullMagnitude > moveMagnitude + 1e-9,
                    commandedSegmentArcseconds = moveMagnitude,
                    raTangentOffsetArcseconds = raArcseconds,
                    decOffsetArcseconds = decArcseconds,
                    boundedCommandRaDegrees = boundedCoordinate.RADegrees,
                    boundedCommandDecDegrees = boundedCoordinate.Dec,
                    reservedReturnRadiusArcseconds = nextRadius,
                    state.DeclaredEvidencePath,
                    sourceSolveEvidencePath = lastQhySolve.EvidencePath,
                    sourceSolveEvidenceSha256 = lastQhySolve.EvidenceSha256,
                },
                lastQhySolve.SourcePath,
                ct).ConfigureAwait(false);

            // Recheck after evidence I/O, then durably arm the anticipated
            // state before the physical command exactly as the legacy guarded
            // path does.
            await RequireImmediatePhysicalActionGatesAsync(context, ct).ConfigureAwait(false);
            sourceBindingGate = await ValidateQhyAcceptedFrameMountBindingForMotionAsync(
                context,
                lastQhyAcquisition,
                sourceFrame,
                lastQhySolve,
                ct).ConfigureAwait(false);
            if (sourceBindingGate.Disposition != GateDisposition.Passed)
            {
                throw new NinaNativeCenteringException(sourceBindingGate.Code, sourceBindingGate.Message, lastQhySolve.SourcePath);
            }

            RegisterQhyCoarseCorrection(moveMagnitude);
            state = state with
            {
                CurrentRaTangentOffsetArcseconds = nextOriginRa,
                CurrentDeclinationOffsetArcseconds = nextOriginDec,
                CumulativeMotionArcseconds = qhyCoarseCumulativeArcseconds,
            };
            pendingQhyCoarseReturn = state;
            Report($"N.I.N.A. 原生居中：执行受限段 {moveMagnitude:F1} arcsec（原生请求 {fullMagnitude:F1}）");
            if (!await telescopeMediator.SlewToCoordinatesAsync(boundedCoordinate, ct).ConfigureAwait(false))
            {
                throw new NinaNativeCenteringException(
                    "QHY_NATIVE_CENTER_SLEW_REJECTED",
                    "N.I.N.A. telescope mediator rejected the guarded native-centering segment.",
                    intentPath);
            }
            await telescopeMediator.WaitForSlew(ct).ConfigureAwait(false);
            state = ReanchorQhyCoarseStateFromReportedPosition(state);
            pendingQhyCoarseReturn = state;
            var reported = telescopeMediator.GetCurrentPosition();
            var arrivalResidual = AngularSeparationArcseconds(reported, boundedCoordinate);
            if (!double.IsFinite(arrivalResidual) || arrivalResidual > MountCommandArrivalToleranceArcseconds)
            {
                throw new NinaNativeCenteringException(
                    "QHY_NATIVE_CENTER_COMMAND_NOT_REACHED",
                    $"The mount stopped {arrivalResidual:F2} arcsec from the guarded N.I.N.A. centering segment.",
                    intentPath);
            }

            await PublishRunJsonEvidenceAsync(
                "qhy-nina-native-centering-move-completed",
                $"Guarded N.I.N.A. native centering segment {qhyCoarseCorrectionAttempts} completed",
                new
                {
                    intentPath,
                    reportedRaDegrees = reported.RADegrees,
                    reportedDecDegrees = reported.Dec,
                    arrivalResidualArcseconds = arrivalResidual,
                    qhyCoarseCumulativeArcseconds,
                    qhyCoarseCorrectionAttempts,
                },
                lastQhySolve.SourcePath,
                ct).ConfigureAwait(false);
            return true;
        }

        try
        {
            var plateSettings = profileService.ActiveProfile.PlateSolveSettings;
            var primarySolver = plateSolverFactory.GetPlateSolver(plateSettings);
            var blindSolver = plateSolverFactory.GetBlindSolver(plateSettings);
            var guardedTelescope = NinaNativeCenteringTelescopeProxy.Create(telescopeMediator, GuardedSlewAsync);
            var solver = plateSolverFactory.GetCenteringSolver(
                primarySolver,
                blindSolver,
                imagingMediator,
                guardedTelescope,
                filterWheelMediator,
                domeMediator,
                domeFollower);
            solver.CaptureSolver = new NinaNativeQhyCaptureSolver(imageSolver, CaptureAndSolveAsync);

            var parameter = new CenterSolveParameter
            {
                Attempts = 1,
                Binning = configuration.Qhy.Binning,
                Coordinates = target,
                DownSampleFactor = configuration.PlateSolver.DownSampleFactor,
                FocalLength = configuration.Qhy.FocalLengthMillimeters,
                MaxObjects = configuration.PlateSolver.MaximumObjects,
                PixelSize = configuration.Qhy.PixelSizeMicrometers,
                ReattemptDelay = TimeSpan.Zero,
                Regions = configuration.PlateSolver.Regions,
                SearchRadius = configuration.PlateSolver.SearchRadiusDegrees,
                Threshold = configuration.Qhy.CenteringToleranceArcseconds / 60d,
                NoSync = true,
                BlindFailoverEnabled = configuration.PlateSolver.BlindFailoverEnabled,
            };
            var sequence = new CaptureSequence(
                configuration.Qhy.AcquisitionExposureLadderSeconds.Last(),
                CaptureSequence.ImageTypes.SNAPSHOT,
                null,
                new BinningMode((short)configuration.Qhy.Binning, (short)configuration.Qhy.Binning),
                1)
            {
                Gain = configuration.Qhy.Gain,
            };
            var nativeResult = await solver.Center(
                sequence,
                parameter,
                solveProgress: null,
                progress,
                cancellationToken).ConfigureAwait(false);
            if (!nativeResult.Success || lastQhySolve is null)
            {
                throw deferredFailure ?? new NinaNativeCenteringException(
                    "QHY_NINA_NATIVE_CENTER_FAILED",
                    "N.I.N.A. native CenteringSolver exhausted its loop without an accepted final QHY WCS.",
                    state.DeclaredEvidencePath);
            }

            var finalResidual = AngularSeparationArcseconds(target, lastQhySolve.Result.Coordinates);
            if (!double.IsFinite(finalResidual) || finalResidual > configuration.Qhy.CenteringToleranceArcseconds)
            {
                throw new NinaNativeCenteringException(
                    "QHY_NINA_NATIVE_CENTER_FINAL_RESIDUAL",
                    $"N.I.N.A. native centering returned success, but the fresh QHY WCS residual is {finalResidual:F2} arcsec.",
                    lastQhySolve.EvidencePath);
            }

            pendingQhyCoarseReturn = null;
            var summaryPath = await PublishRunJsonEvidenceAsync(
                "qhy-nina-native-centering-summary",
                "N.I.N.A. native QHY centering completed inside the independent motion envelope",
                new
                {
                    engine = "NINA.PlateSolving.CenteringSolver",
                    success = true,
                    finalResidualArcseconds = finalResidual,
                    toleranceArcseconds = configuration.Qhy.CenteringToleranceArcseconds,
                    captureCount = captureNumber,
                    qhyCoarseCumulativeArcseconds,
                    qhyCoarseCorrectionAttempts,
                    finalSolveEvidencePath = lastQhySolve.EvidencePath,
                    finalSolveEvidenceSha256 = lastQhySolve.EvidenceSha256,
                    declaredPath,
                },
                lastQhySolve.SourcePath,
                cancellationToken).ConfigureAwait(false);
            return Passed(
                "QHY_NINA_NATIVE_CENTERED",
                $"N.I.N.A. native centering reached a fresh QHY WCS residual of {finalResidual:F2} arcsec in {qhyCoarseCorrectionAttempts} guarded segment(s).",
                new Dictionary<string, double>
                {
                    ["residualArcseconds"] = finalResidual,
                    ["qhyCoarseCumulativeCorrectionArcseconds"] = qhyCoarseCumulativeArcseconds,
                    ["qhyCoarseCorrectionAttempts"] = qhyCoarseCorrectionAttempts,
                    ["ninaNativeCaptureCount"] = captureNumber,
                },
                new Dictionary<string, string>
                {
                    ["centeringEngine"] = "NINA.PlateSolving.CenteringSolver",
                    ["solver"] = lastQhySolve.SolverIdentity,
                    ["summaryEvidencePath"] = summaryPath,
                    ["qhyCoarseCenteringSchemaVersion"] = limits.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = ex as NinaNativeCenteringException ?? deferredFailure ?? new NinaNativeCenteringException(
                "QHY_NINA_NATIVE_CENTER_EXCEPTION",
                $"N.I.N.A. native QHY centering stopped: {ex.Message}",
                state.DeclaredEvidencePath,
                ex);
            return await StopQhyCoarseAndReturnAsync(
                context,
                state,
                failure.Code,
                failure.Message,
                failure.EvidencePath ?? state.DeclaredEvidencePath,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            context.RemainingWorstCaseDuration = previousWorstCaseDuration;
        }
    }
}
