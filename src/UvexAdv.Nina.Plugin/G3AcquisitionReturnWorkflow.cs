using NINA.Astrometry;
using UvexAdv.Observatory;
using static UvexAdv.Nina.Plugin.ObservationMotionGeometry;

namespace UvexAdv.Nina.Plugin;

internal sealed record G3AcquisitionReturnSettings(double FreshSolveAuthorizationResidualArcseconds, double PostSlewSettleSeconds);

// Owns the return state machine and conservative intent/precharge ordering.
// It receives the existing lineage and returns it without clearing or rebasing it.
internal sealed class G3AcquisitionReturnWorkflow(G3AcquisitionReturnSettings settings,
    IG3AcquisitionReturnPort port, TimeProvider? timeProvider = null)
{
    private const double MountCommandArrivalToleranceArcseconds = 2d;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    internal async Task<G3AcquisitionMotionReturnResult> ReturnAsync(
        ObservationContext context,
        G3AcquisitionMotionState state,
        string path,
        CancellationToken cancellationToken,
        Phd2DependencyRebuildStopProof? stoppedGuideProof = null)
    {
        var stableNearOriginToleranceArcseconds =
            G3AcquisitionMotionPlanner.ComputeStableNearOriginToleranceArcseconds(
                state,
                settings.FreshSolveAuthorizationResidualArcseconds);
        if (!double.IsFinite(stableNearOriginToleranceArcseconds) ||
            stableNearOriginToleranceArcseconds < MountCommandArrivalToleranceArcseconds)
        {
            return new G3AcquisitionMotionReturnResult(
                false,
                state,
                path,
                "The stable near-origin return tolerance is invalid; no automatic return was attempted.");
        }

        var stableNearOriginReadbackAccepted = false;
        while (true)
        {
            if (stoppedGuideProof is not null && !stoppedGuideProof.IsCurrent(port.GuideSnapshot))
                return new(false, state, path, "G3_GUIDING_RECOVERY_STOP_CHANGED: PHD2 changed after confirmed stop; return withheld.");
            Coordinates reported;
            try
            {
                reported = port.GetCurrentPosition();
                state = ReanchorG3AcquisitionMotionFromReportedPosition(state, reported);
            }
            catch (Exception ex)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, ex.Message);
            }
            var mountGate = port.ValidateG3SearchMountState(state.PierSide);
            if (mountGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{mountGate.Code}: {mountGate.Message}", mountGate);
            }

            // A driver can finish a long segmented return a fraction of an
            // arcsecond away from the exact saved coordinate.  If the strict
            // action budget is already consumed, do not manufacture one more
            // mount action for that harmless sub-arcsecond readback.  Prove
            // that the mount is stable, unchanged in epoch/pier side and still
            // inside the separately bounded near-origin envelope, then settle
            // the durable ledger without issuing another command.
            if (!stableNearOriginReadbackAccepted &&
                state.CurrentRadiusArcseconds > MountCommandArrivalToleranceArcseconds &&
                state.CurrentRadiusArcseconds <= stableNearOriginToleranceArcseconds)
            {
                var originCoordinate = new Coordinates(
                    state.OriginRaDegrees,
                    state.OriginDeclinationDegrees,
                    reported.Epoch,
                    Coordinates.RAType.Degrees);
                var stableNearOrigin = await port.WaitForG3PostSlewStabilityAsync(
                    context,
                    originCoordinate,
                    reported,
                    state.PierSide,
                    state.CoordinateEpoch,
                    "durable G3 no-motion near-origin verification",
                    stableNearOriginToleranceArcseconds,
                    cancellationToken).ConfigureAwait(false);
                if (stableNearOrigin.Gate.Disposition != GateDisposition.Passed ||
                    stableNearOrigin.Reported is null)
                {
                    return new G3AcquisitionMotionReturnResult(
                        false,
                        state,
                        path,
                        $"{stableNearOrigin.Gate.Code}: {stableNearOrigin.Gate.Message}");
                }

                state = ReanchorG3AcquisitionMotionFromReportedPosition(state, stableNearOrigin.Reported) with
                {
                    UpdatedUtc = clock.GetUtcNow(),
                    LastReason = $"No-motion return verification remained stable at {state.CurrentRadiusArcseconds:F3} arcsec from origin; the {stableNearOriginToleranceArcseconds:F2} arcsec near-origin settlement envelope is authorized without consuming another action.",
                };
                await port.PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);
                stableNearOriginReadbackAccepted = true;
                continue;
            }
            var step = G3AcquisitionMotionPlanner.PlanNextReturnStep(
                state,
                NormalizeDegrees(reported.RADegrees),
                reported.Dec,
                stableNearOriginToleranceArcseconds,
                clock.GetUtcNow());
            if (step.Gate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{step.Gate.Code}: {step.Gate.Message}");
            }
            if (step.AlreadyAtOrigin)
            {
                state = state with
                {
                    Phase = G3AcquisitionMotionPhase.SettledBudgetLedger,
                    PriorReportedRaDegrees = NormalizeDegrees(reported.RADegrees),
                    PriorReportedDeclinationDegrees = reported.Dec,
                    CommandedRaDegrees = NormalizeDegrees(reported.RADegrees),
                    CommandedDeclinationDegrees = reported.Dec,
                    CommandMagnitudeArcseconds = 0,
                    UpdatedUtc = clock.GetUtcNow(),
                    LastReason = stableNearOriginReadbackAccepted
                        ? $"The durable G3 acquisition return settled without another command at the stable reported position {state.CurrentRadiusArcseconds:F2} arcsec from its saved origin; the real offset remains charged and no budget was reset."
                        : $"The durable G3 acquisition return reached a reported position {state.CurrentRadiusArcseconds:F2} arcsec from its saved origin; the real readback remains charged and no budget was reset.",
                };
                await port.PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);
                return new G3AcquisitionMotionReturnResult(true, state, path, state.LastReason!);
            }

            try
            {
                await port.RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (ResumeStageRestartException) { throw; }
            catch (PhysicalActionGateException ex)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{ex.Gate.Code}: {ex.Gate.Message}");
            }
            var commanded = new Coordinates(
                step.CommandedRaDegrees,
                step.CommandedDeclinationDegrees,
                reported.Epoch,
                Coordinates.RAType.Degrees);
            var reportedBeforeIntent = port.GetCurrentPosition();
            var intentMountGate = port.ValidateG3SearchMountState(state.PierSide);
            if (intentMountGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{intentMountGate.Code}: {intentMountGate.Message}", intentMountGate);
            }
            if (!string.Equals(state.CoordinateEpoch, reportedBeforeIntent.Epoch.ToString(), StringComparison.Ordinal) ||
                AngularSeparationArcseconds(reported, reportedBeforeIntent) > state.ArrivalToleranceArcseconds)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, "The fresh reported mount coordinate changed beyond the reserved arrival tolerance before the durable return intent; the intent was not written.");
            }
            var reportedIntentHorizon = port.ValidateCommandCoordinateHorizon(
                context,
                reportedBeforeIntent,
                "durable G3 acquisition fresh reported position before return intent");
            if (reportedIntentHorizon.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{reportedIntentHorizon.Code}: {reportedIntentHorizon.Message}");
            }
            var horizonGate = port.ValidateCommandCoordinateHorizon(context, commanded, "durable G3 acquisition return move");
            if (horizonGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{horizonGate.Code}: {horizonGate.Message}");
            }
            var sphericalIntentGate = G3AcquisitionMotionPlanner.ValidateSphericalCommand(
                state,
                NormalizeDegrees(reportedBeforeIntent.RADegrees),
                reportedBeforeIntent.Dec,
                NormalizeDegrees(commanded.RADegrees),
                commanded.Dec,
                step.CommandMagnitudeArcseconds);
            if (sphericalIntentGate.Gate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{sphericalIntentGate.Gate.Code}: {sphericalIntentGate.Gate.Message}");
            }

            // ValidateOutboundAndReturnReserve reserves every return segment at
            // the full durable single-motion ceiling. Consume that same amount
            // before the asynchronous command. A near-origin stable readback
            // may be wider than the strict two-arcsecond arrival tolerance, so
            // command+tolerance alone is not a complete crash-safe charge.
            var destinationSideGate = port.ValidateG3DestinationPierSide(commanded, state.PierSide);
            if (destinationSideGate.Disposition != GateDisposition.Passed)
                return new(false, state, path, $"{destinationSideGate.Code}: {destinationSideGate.Message}");
            var fullyChargedReturnArcseconds = state.MaximumSingleCorrectionArcseconds;
            if (state.CumulativeMotionArcseconds + fullyChargedReturnArcseconds >
                state.MaximumCumulativeMotionArcseconds + 1e-9)
            {
                return new G3AcquisitionMotionReturnResult(
                    false,
                    state,
                    path,
                    "G3_MOTION_RETURN_FULL_PRECHARGE_LIMIT: The next return command was withheld because its fully charged single-motion reserve no longer fits the durable cumulative envelope.");
            }
            // Conservatively consume the action and the complete reserved
            // segment before persisting the intent and before the command.
            state = state with
            {
                Phase = G3AcquisitionMotionPhase.ReturnIntent,
                PriorReportedRaDegrees = NormalizeDegrees(reportedBeforeIntent.RADegrees),
                PriorReportedDeclinationDegrees = reportedBeforeIntent.Dec,
                CommandedRaDegrees = NormalizeDegrees(commanded.RADegrees),
                CommandedDeclinationDegrees = commanded.Dec,
                CommandMagnitudeArcseconds = step.CommandMagnitudeArcseconds,
                CumulativeMotionArcseconds = state.CumulativeMotionArcseconds +
                    fullyChargedReturnArcseconds,
                CorrectionAttempts = state.CorrectionAttempts + 1,
                UpdatedUtc = clock.GetUtcNow(),
                LastReason = $"Durable return intent for {step.CommandMagnitudeArcseconds:F2} arcsec fully precharged the reserved {fullyChargedReturnArcseconds:F2} arcsec segment before command dispatch.",
            };
            await port.PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);

            await port.RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
            var immediatelyBefore = port.GetCurrentPosition();
            if (stoppedGuideProof is not null && !stoppedGuideProof.IsCurrent(port.GuideSnapshot))
                return new(false, state, path, "G3_GUIDING_RECOVERY_STOP_CHANGED: PHD2 changed before return dispatch; precharge retained.");
            var precommandMountGate = port.ValidateG3SearchMountState(state.PierSide);
            if (precommandMountGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{precommandMountGate.Code}: {precommandMountGate.Message}", precommandMountGate);
            }
            if (!string.Equals(state.CoordinateEpoch, immediatelyBefore.Epoch.ToString(), StringComparison.Ordinal))
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, "The mount coordinate epoch changed after the durable return intent; the command was withheld.");
            }
            if (AngularSeparationArcseconds(reportedBeforeIntent, immediatelyBefore) > MountCommandArrivalToleranceArcseconds)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, "The reported mount position changed after the durable return intent; the stale absolute command was withheld.");
            }
            var freshReportedHorizon = port.ValidateCommandCoordinateHorizon(
                context,
                immediatelyBefore,
                "durable G3 acquisition fresh reported position before return command");
            if (freshReportedHorizon.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{freshReportedHorizon.Code}: {freshReportedHorizon.Message}");
            }
            var freshSphericalGate = G3AcquisitionMotionPlanner.ValidateSphericalCommand(
                state,
                NormalizeDegrees(immediatelyBefore.RADegrees),
                immediatelyBefore.Dec,
                NormalizeDegrees(commanded.RADegrees),
                commanded.Dec,
                sphericalIntentGate.CommandDistanceArcseconds);
            if (freshSphericalGate.Gate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{freshSphericalGate.Gate.Code}: {freshSphericalGate.Gate.Message}");
            }
            var finalHorizonGate = port.ValidateCommandCoordinateHorizon(context, commanded, "durable G3 acquisition final return check");
            if (finalHorizonGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{finalHorizonGate.Code}: {finalHorizonGate.Message}");
            }
            destinationSideGate = port.ValidateG3DestinationPierSide(commanded, state.PierSide);
            if (destinationSideGate.Disposition != GateDisposition.Passed)
                return new(false, state, path, $"{destinationSideGate.Code}: {destinationSideGate.Message} Precharge retained.");
            if (!await port.SlewToCoordinatesAsync(commanded, cancellationToken).ConfigureAwait(false))
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, "N.I.N.A. rejected the durable G3 return command; its precharged intent remains inspectable.");
            }
            await port.WaitForSlew(cancellationToken).ConfigureAwait(false);
            var after = port.GetCurrentPosition();
            if (!string.Equals(state.CoordinateEpoch, after.Epoch.ToString(), StringComparison.Ordinal))
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, "The mount coordinate epoch changed after the return command; no arrival was accepted.");
            }
            var afterMountGate = port.ValidateG3SearchMountState(state.PierSide);
            if (afterMountGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{afterMountGate.Code}: {afterMountGate.Message}", afterMountGate);
            }
            var reportedHorizonGate = port.ValidateCommandCoordinateHorizon(context, after, "durable G3 acquisition reported return arrival");
            if (reportedHorizonGate.Disposition != GateDisposition.Passed)
            {
                return new G3AcquisitionMotionReturnResult(false, state, path, $"{reportedHorizonGate.Code}: {reportedHorizonGate.Message}");
            }
            var commandResidual = AngularSeparationArcseconds(after, commanded);
            var actualReturnMoveArcseconds = AngularSeparationArcseconds(immediatelyBefore, after);
            state = ReanchorG3AcquisitionMotionFromReportedPosition(state, after) with
            {
                LastReason = $"Return command completed with {commandResidual:F2} arcsec reported residual and {actualReturnMoveArcseconds:F2} arcsec actual movement; the full {fullyChargedReturnArcseconds:F2} arcsec segment remains charged.",
            };
            await port.PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);
            if (!double.IsFinite(commandResidual) ||
                !double.IsFinite(actualReturnMoveArcseconds) ||
                actualReturnMoveArcseconds > fullyChargedReturnArcseconds + 1e-9)
            {
                return new G3AcquisitionMotionReturnResult(
                    false,
                    state,
                    path,
                    $"The durable G3 return readback is outside its fully charged segment (command residual {commandResidual:F2} arcsec, actual move {actualReturnMoveArcseconds:F2} arcsec, limit {fullyChargedReturnArcseconds:F2}); reported coordinates were retained and further automatic motion stopped.");
            }

            var commandTargetsDurableOrigin =
                AngularSeparationArcseconds(commanded, new Coordinates(
                    state.OriginRaDegrees,
                    state.OriginDeclinationDegrees,
                    commanded.Epoch,
                    Coordinates.RAType.Degrees)) <= 1e-6;
            var returnCommandResidualToleranceArcseconds = commandTargetsDurableOrigin
                ? stableNearOriginToleranceArcseconds
                : MountCommandArrivalToleranceArcseconds;
            var stability = await port.WaitForG3PostSlewStabilityAsync(
                context,
                commanded,
                after,
                state.PierSide,
                state.CoordinateEpoch,
                "durable G3 return",
                returnCommandResidualToleranceArcseconds,
                cancellationToken).ConfigureAwait(false);
            if (stability.Reported is null)
            {
                return new G3AcquisitionMotionReturnResult(
                    false,
                    state,
                    path,
                    $"{stability.Gate.Code}: {stability.Gate.Message}");
            }

            var settledActualReturnMoveArcseconds = AngularSeparationArcseconds(
                immediatelyBefore,
                stability.Reported);
            var intermediateResidualExceeded =
                !commandTargetsDurableOrigin &&
                double.IsFinite(stability.CommandResidualArcseconds) &&
                stability.CommandResidualArcseconds > MountCommandArrivalToleranceArcseconds + 1e-9;
            state = ReanchorG3AcquisitionMotionFromReportedPosition(state, stability.Reported) with
            {
                LastReason = stability.Gate.Disposition == GateDisposition.Passed
                    ? $"Return command remained stable for {settings.PostSlewSettleSeconds:F2}s with {stability.CommandResidualArcseconds:F2} arcsec residual and {settledActualReturnMoveArcseconds:F2} arcsec total settled movement."
                    : intermediateResidualExceeded
                        ? $"Intermediate return command remained {stability.CommandResidualArcseconds:F2} arcsec from its endpoint, outside the strict {MountCommandArrivalToleranceArcseconds:F2} arcsec segment contract; the reported position was retained and no next return segment is authorized."
                    : $"Return command settled outside its {returnCommandResidualToleranceArcseconds:F2} arcsec command envelope with {stability.CommandResidualArcseconds:F2} arcsec residual and {settledActualReturnMoveArcseconds:F2} arcsec total settled movement; another bounded correction will be considered.",
            };
            await port.PersistG3AcquisitionMotionAsync(state, CancellationToken.None).ConfigureAwait(false);

            if (!double.IsFinite(settledActualReturnMoveArcseconds) ||
                settledActualReturnMoveArcseconds > fullyChargedReturnArcseconds + 1e-9)
            {
                return new G3AcquisitionMotionReturnResult(
                    false,
                    state,
                    path,
                    $"The settled durable G3 return moved {settledActualReturnMoveArcseconds:F2} arcsec, outside its fully charged {fullyChargedReturnArcseconds:F2} arcsec segment; the reported position was retained and further automatic motion stopped.");
            }

            if (intermediateResidualExceeded)
            {
                var previousRadius = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
                    state.OriginRaDegrees, state.OriginDeclinationDegrees,
                    NormalizeDegrees(immediatelyBefore.RADegrees), immediatelyBefore.Dec);
                if (G3AcquisitionMotionPlanner.CanReplanStableIntermediateReturn(
                    state, previousRadius, stability.CommandResidualArcseconds,
                    stability.ReportedDriftArcseconds, settledActualReturnMoveArcseconds))
                {
                    // Do not accept the missed nominal endpoint or settle the
                    // ledger. The fully precharged movement made independently
                    // measured progress; replan from the fresh stable readback.
                    // The next iteration reserves and charges another action
                    // under the unchanged lineage limits and earliest clock.
                    await port.PublishRunJsonEvidenceAsync(
                        "g3-intermediate-return-replanned",
                        "A bounded nominal-endpoint miss replanned the remaining return from fresh measured position",
                        new { previousRadius, remainingRadius = state.CurrentRadiusArcseconds,
                            stability.CommandResidualArcseconds, stability.ReportedDriftArcseconds,
                            settledActualReturnMoveArcseconds, fullyChargedReturnArcseconds,
                            nominalEndpointAccepted = false, budgetReset = false },
                        null, cancellationToken).ConfigureAwait(false);
                    port.Report($"G3 回程中间点偏差 {stability.CommandResidualArcseconds:F2}″；新回读稳定且实际运动已完整计费，从剩余 {state.CurrentRadiusArcseconds:F2}″ 重新规划回程，不放宽最终回原门限");
                    stableNearOriginReadbackAccepted = false;
                    continue;
                }
                return new G3AcquisitionMotionReturnResult(
                    false,
                    state,
                    path,
                    $"G3_MOTION_RETURN_INTERMEDIATE_RESIDUAL_LIMIT: The non-origin return segment retained a {stability.CommandResidualArcseconds:F2} arcsec residual, exceeding the strict {MountCommandArrivalToleranceArcseconds:F2} arcsec endpoint limit; the fresh reported position was preserved and no further return command was issued.");
            }

            if (stability.Gate.Disposition == GateDisposition.Passed)
            {
                // Only a stable command whose endpoint is the durable origin
                // may activate the wider no-motion final settlement. An
                // intermediate segmented return remains on the strict 2-arcsec
                // arrival contract used to reserve its guaranteed progress.
                stableNearOriginReadbackAccepted = commandTargetsDurableOrigin;
                continue;
            }

            if (!double.IsFinite(stability.ReportedDriftArcseconds) ||
                stability.ReportedDriftArcseconds > MountCommandArrivalToleranceArcseconds ||
                !double.IsFinite(stability.CommandResidualArcseconds))
            {
                return new G3AcquisitionMotionReturnResult(
                    false,
                    state,
                    path,
                    $"{stability.Gate.Code}: {stability.Gate.Message}");
            }

            // The mount is stable but still outside the near-origin envelope.
            // Keep the durable obligation and let the existing planner consume
            // another bounded correction; its cumulative, attempt and elapsed
            // limits prevent an unbounded retry loop.
            stableNearOriginReadbackAccepted = false;
        }
    }
}
