using System.Globalization;
using System.IO;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NINA.Astrometry;
using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Opportunistic, same-pointing QHY/G3 solve-pair collection plus the explicitly
/// separate no-mechanical-home coordinate recovery from ADR-0012.  The pair
/// collector itself may request one QHY-service-owned exposure but contains no
/// mount command and produces a Candidate only.  Coordinate recovery can issue
/// one N.I.N.A.-mediated Sync and reissue the catalogue slew; it never promotes
/// a pair Candidate to motion authority or substitutes it for G3PixelToMount.
/// </summary>
internal sealed partial class RealObservationStageRunner
{
    private readonly ConcurrentDictionary<string, byte> attemptedQhyG3SolvePairFrames = new(StringComparer.OrdinalIgnoreCase);
    private Guid? activeQhyG3FastPairJobId;

    /// <summary>
    /// Selects the sky-coordinate hint for the long-focus G3 solve.  A fresh,
    /// immutable, same-pointing QHY/PL3 solution is the preferred sky truth on
    /// mounts which can start with an inaccurate absolute coordinate after a
    /// manual zero.  A gross mismatch is corrected by one N.I.N.A.-mediated
    /// Sync at a verified stationary pointing; ordinary small differences only
    /// change the solver hint.  Neither path gives QHY mount-motion ownership.
    /// </summary>
    private async Task<G3PlateSolveHintSelection> SelectG3PlateSolveHintAsync(
        ObservationContext context,
        CancellationToken cancellationToken)
    {
        var target = TargetCoordinates(context.Plan);
        if (lastQhySolve?.Result.Success != true ||
            lastQhySolve.Result.Coordinates is null ||
            lastQhyAcquisition?.AcceptedFrameId is not { } frameId)
        {
            return new G3PlateSolveHintSelection(
                target,
                "PlannedCatalogTargetFallback",
                "No current-run accepted QHY frame with a formal PL3 solution is available; the planned catalogue coordinate remains the solve hint.");
        }

        var accepted = lastQhyAcquisition.Frames.SingleOrDefault(frame => frame.FrameId == frameId);
        if (accepted is null)
        {
            return new G3PlateSolveHintSelection(
                target,
                "PlannedCatalogTargetFallback",
                "The accepted QHY frame id is absent from its immutable job manifest.");
        }

        var gate = await ValidateQhyAcceptedFrameMountBindingForMotionAsync(
            context,
            lastQhyAcquisition,
            accepted,
            lastQhySolve,
            cancellationToken).ConfigureAwait(false);
        if (gate.Disposition != GateDisposition.Passed)
        {
            await WriteAuditBestEffortAsync("g3-solve-hint-qhy-skipped", new
            {
                gate.Code,
                gate.Message,
                qhyFrame = accepted.FitsPath,
                qhySolveEvidence = lastQhySolve.EvidencePath,
                mountSyncCommanded = false,
            }).ConfigureAwait(false);
            return new G3PlateSolveHintSelection(
                target,
                "PlannedCatalogTargetFallback",
                $"The available QHY WCS was not fresh at the unchanged current pointing ({gate.Code}); the planned catalogue coordinate remains the solve hint.");
        }

        var qhyCoordinates = lastQhySolve.Result.Coordinates;
        var targetResidual = AngularSeparationArcseconds(target, qhyCoordinates);
        var coordinateRecovery = await RecoverMountCoordinatesFromQhyWcsIfRequiredAsync(
            context,
            accepted,
            lastQhySolve,
            qhyCoordinates,
            cancellationToken).ConfigureAwait(false);
        var selectedHintCoordinates = coordinateRecovery.SyncVerified
            ? target
            : qhyCoordinates;
        var selectedHintAuthority = coordinateRecovery.SyncVerified
            ? "CatalogTargetAfterFreshQhyWcsMountSync"
            : coordinateRecovery.SyncCommanded
                ? "FreshQhyPl3WcsAfterUnverifiedMountSync"
                : "FreshQhyPl3Wcs";
        context.Set("g3PlateSolveHintAuthority", selectedHintAuthority);
        context.Set("g3PlateSolveHintQhyTargetResidualArcseconds", targetResidual);
        context.Set("qhyMountCoordinateAuthorityOutcome", coordinateRecovery.Outcome);
        Report(
            $"G3 解算提示改用本轮新鲜 QHY/PL3 天空坐标；其与目标相差 {targetResidual / 3600d:F2}°。" +
            $"该差值不写成两镜光轴差；赤道仪坐标处理：{coordinateRecovery.Message}");
        await WriteAuditBestEffortAsync("g3-solve-hint-fresh-qhy-pl3", new
        {
            qhyFrame = accepted.FitsPath,
            qhyFrameSha256 = accepted.Sha256,
            qhySolveEvidence = lastQhySolve.EvidencePath,
            qhySolveEvidenceSha256 = lastQhySolve.EvidenceSha256,
            qhySolvedRaDegrees = qhyCoordinates.RADegrees,
            qhySolvedDecDegrees = qhyCoordinates.Dec,
            plannedTargetRaDegrees = target.RADegrees,
            plannedTargetDecDegrees = target.Dec,
            targetResidualArcseconds = targetResidual,
            skyCoordinateAuthority = selectedHintAuthority,
            mountCoordinateAuthorityOutcome = coordinateRecovery.Outcome,
            coordinateRecovery.MountResidualBeforeArcseconds,
            coordinateRecovery.MountResidualAfterArcseconds,
            mountSyncCommanded = coordinateRecovery.SyncCommanded,
            mountSyncVerified = coordinateRecovery.SyncVerified,
            mountMotionAuthority = false,
        }).ConfigureAwait(false);
        return new G3PlateSolveHintSelection(
            selectedHintCoordinates,
            selectedHintAuthority,
            coordinateRecovery.SyncVerified
                ? $"Fresh immutable QHY/PL3 WCS repaired the mount coordinate and the catalogue slew was reissued; G3 now solves around the planned target. The original {targetResidual:F1} arcsec target residual is not optical-axis separation."
                : coordinateRecovery.SyncCommanded
                    ? $"The one allowed stationary mount Sync was not verified and will not be repeated. Fresh immutable QHY/PL3 WCS remains a read-only G3 solve hint at the unchanged physical pointing; no catalogue slew or motion authority was granted."
                    : $"Fresh immutable QHY/PL3 WCS selected at the unchanged current pointing; target residual {targetResidual:F1} arcsec is not interpreted as optical-axis separation.");
    }

    private async Task<QhyMountCoordinateRecoveryResult> RecoverMountCoordinatesFromQhyWcsIfRequiredAsync(
        ObservationContext context,
        QhyFrameRecord accepted,
        PlateSolveEvidence qhySolve,
        Coordinates qhyCoordinates,
        CancellationToken cancellationToken)
    {
        var beforeInfo = telescopeMediator.GetInfo();
        if (!beforeInfo.Connected || beforeInfo.AtPark || beforeInfo.Slewing || beforeInfo.IsPulseGuiding)
        {
            throw new PhysicalActionGateException(GateResult.Unknown(
                "QHY_MOUNT_COORDINATE_SYNC_STATE_INVALID",
                $"Fresh QHY WCS cannot seed the mount coordinate while connected={beforeInfo.Connected}, parked={beforeInfo.AtPark}, slewing={beforeInfo.Slewing}, pulseGuiding={beforeInfo.IsPulseGuiding}."));
        }

        var before = telescopeMediator.GetCurrentPosition();
        EnsureFiniteReportedCoordinates(before);
        var beforePierSide = beforeInfo.SideOfPier.ToString();
        if (!IsKnownPierSide(beforePierSide))
        {
            throw new PhysicalActionGateException(GateResult.Unknown(
                "QHY_MOUNT_COORDINATE_SYNC_PIER_UNKNOWN",
                "Fresh QHY WCS cannot seed the mount coordinate while the pier side is unknown."));
        }

        // PL3/WCS is the physical sky truth, but ASCOM Sync consumes coordinates
        // in the mount driver's currently reported epoch.  Keep an explicit
        // J2000 authority record, then derive every mount-facing coordinate from
        // that truth in the corresponding live readback epoch.  Passing the raw
        // J2000 numbers to a JNOW driver creates an ordinary-precession offset.
        var qhyTruthJ2000 = qhyCoordinates.Epoch == Epoch.J2000
            ? qhyCoordinates
            : qhyCoordinates.Transform(Epoch.J2000);
        var qhyTruthAtBeforeEpoch = qhyTruthJ2000.Epoch == before.Epoch
            ? qhyTruthJ2000
            : qhyTruthJ2000.Transform(before.Epoch);
        var residualBefore = AngularSeparationArcseconds(before, qhyTruthAtBeforeEpoch);
        var thresholdArcseconds = configuration.G3.MaximumPlateSolveHintOffsetDegrees * 3600d;
        if (!double.IsFinite(residualBefore) || !double.IsFinite(thresholdArcseconds) || thresholdArcseconds <= 0)
        {
            throw new PhysicalActionGateException(GateResult.Unknown(
                "QHY_MOUNT_COORDINATE_SYNC_RESIDUAL_INVALID",
                "The QHY-to-mount coordinate residual or configured gross-mismatch threshold is invalid."));
        }
        if (residualBefore <= thresholdArcseconds)
        {
            return new QhyMountCoordinateRecoveryResult(
                "FreshQhyWcsHintOnly",
                false,
                false,
                residualBefore,
                null,
                $"差 {residualBefore / 3600d:F3}°，未超过 {configuration.G3.MaximumPlateSolveHintOffsetDegrees:F2}°，不 Sync");
        }

        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        // The physical-action gate may itself refresh/reconnect the mount.  Take
        // a new readback immediately before durable intent and bind the one Sync
        // command to that live epoch, rather than to the earlier gate snapshot.
        var syncCommandReadback = telescopeMediator.GetCurrentPosition();
        EnsureFiniteReportedCoordinates(syncCommandReadback);
        var qhySyncCommandCoordinates = qhyTruthJ2000.Epoch == syncCommandReadback.Epoch
            ? qhyTruthJ2000
            : qhyTruthJ2000.Transform(syncCommandReadback.Epoch);
        var intentPath = await PublishRunJsonEvidenceAsync(
            "qhy-mount-coordinate-sync-intent",
            "One-time stationary mount coordinate recovery from fresh QHY/PL3 WCS",
            new
            {
                authority = "Fresh current-run QHY/PL3 formal WCS",
                qhyFrame = accepted.FitsPath,
                qhyFrameSha256 = accepted.Sha256,
                qhySolveEvidence = qhySolve.EvidencePath,
                qhySolveEvidenceSha256 = qhySolve.EvidenceSha256,
                before = new { raDegrees = before.RADegrees, decDegrees = before.Dec, epoch = before.Epoch.ToString() },
                qhyTruth = new { raDegrees = qhyCoordinates.RADegrees, decDegrees = qhyCoordinates.Dec, epoch = qhyCoordinates.Epoch.ToString() },
                qhyTruthJ2000 = new
                {
                    raDegrees = qhyTruthJ2000.RADegrees,
                    decDegrees = qhyTruthJ2000.Dec,
                    epoch = qhyTruthJ2000.Epoch.ToString(),
                },
                syncCommandMountReadback = new
                {
                    raDegrees = syncCommandReadback.RADegrees,
                    decDegrees = syncCommandReadback.Dec,
                    epoch = syncCommandReadback.Epoch.ToString(),
                },
                qhySyncCommand = new
                {
                    raDegrees = qhySyncCommandCoordinates.RADegrees,
                    decDegrees = qhySyncCommandCoordinates.Dec,
                    epoch = qhySyncCommandCoordinates.Epoch.ToString(),
                },
                pierSide = beforePierSide,
                residualBeforeArcseconds = residualBefore,
                grossMismatchThresholdArcseconds = thresholdArcseconds,
                syncAttemptOrdinal = 1,
                mountSlewCommandCount = 0,
            },
            accepted.FitsPath,
            cancellationToken).ConfigureAwait(false);

        // Consume the one-shot allowance only after all pre-action gates and
        // durable intent evidence have succeeded.  A weather/safety refusal or
        // an evidence-write problem must not pretend that a Sync was attempted.
        if (Interlocked.CompareExchange(ref qhyMountCoordinateSyncPerformed, 1, 0) != 0)
        {
            return await ContinueWithFreshQhyHintAfterUnverifiedSyncAsync(
                context,
                accepted,
                qhySolve,
                qhyCoordinates,
                "QHY_MOUNT_COORDINATE_SYNC_REPEAT_BLOCKED",
                $"The mount still differs from the fresh QHY WCS by {residualBefore / 3600d:F3} degrees after this run already consumed its one coordinate Sync attempt. Repeating Sync is prohibited; the fresh QHY WCS remains solve-hint-only.",
                beforePierSide,
                residualBefore,
                null,
                syncCommanded: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        bool acceptedByNina;
        try
        {
            acceptedByNina = await telescopeMediator.Sync(qhySyncCommandCoordinates).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await ContinueWithFreshQhyHintAfterUnverifiedSyncAsync(
                context,
                accepted,
                qhySolve,
                qhyCoordinates,
                "QHY_MOUNT_COORDINATE_SYNC_EXCEPTION",
                $"N.I.N.A./ASCOM raised {ex.GetType().Name} while applying the one-time QHY WCS coordinate Sync: {ex.Message}. No slew was commanded; the one-shot allowance remains consumed.",
                beforePierSide,
                residualBefore,
                null,
                syncCommanded: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        if (!acceptedByNina)
        {
            return await ContinueWithFreshQhyHintAfterUnverifiedSyncAsync(
                context,
                accepted,
                qhySolve,
                qhyCoordinates,
                "QHY_MOUNT_COORDINATE_SYNC_REJECTED",
                "N.I.N.A./ASCOM rejected the one-time QHY WCS coordinate Sync. No slew was commanded and the one-shot allowance remains consumed.",
                beforePierSide,
                residualBefore,
                null,
                syncCommanded: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        Coordinates after = telescopeMediator.GetCurrentPosition();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        var qhyTruthAtReadbackEpoch = qhyTruthJ2000.Epoch == after.Epoch
            ? qhyTruthJ2000
            : qhyTruthJ2000.Transform(after.Epoch);
        var residualAfter = AngularSeparationArcseconds(after, qhyTruthAtReadbackEpoch);
        while ((!double.IsFinite(residualAfter) || residualAfter > 5d) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            after = telescopeMediator.GetCurrentPosition();
            qhyTruthAtReadbackEpoch = qhyTruthJ2000.Epoch == after.Epoch
                ? qhyTruthJ2000
                : qhyTruthJ2000.Transform(after.Epoch);
            residualAfter = AngularSeparationArcseconds(after, qhyTruthAtReadbackEpoch);
        }
        var afterInfo = telescopeMediator.GetInfo();
        var afterPierSide = afterInfo.SideOfPier.ToString();
        if (!afterInfo.Connected || afterInfo.AtPark || afterInfo.Slewing ||
            !string.Equals(beforePierSide, afterPierSide, StringComparison.OrdinalIgnoreCase) ||
            !double.IsFinite(residualAfter) || residualAfter > 5d)
        {
            var readbackFailure =
                $"N.I.N.A. accepted QHY WCS Sync but same-epoch readback did not verify: connected={afterInfo.Connected}, parked={afterInfo.AtPark}, slewing={afterInfo.Slewing}, pier={beforePierSide}->{afterPierSide}, " +
                $"readbackEpoch={after.Epoch}, QHY {qhyCoordinates.Epoch}->{qhyTruthAtReadbackEpoch.Epoch}, residual={residualAfter:F2} arcsec (limit 5.00). No slew was commanded.";
            if (!afterInfo.Connected || afterInfo.AtPark || afterInfo.Slewing ||
                !string.Equals(beforePierSide, afterPierSide, StringComparison.OrdinalIgnoreCase))
            {
                throw new PhysicalActionGateException(GateResult.Unknown(
                    "QHY_MOUNT_COORDINATE_SYNC_READBACK_FAILED",
                    readbackFailure));
            }
            return await ContinueWithFreshQhyHintAfterUnverifiedSyncAsync(
                context,
                accepted,
                qhySolve,
                qhyCoordinates,
                "QHY_MOUNT_COORDINATE_SYNC_READBACK_FAILED",
                readbackFailure,
                beforePierSide,
                residualBefore,
                residualAfter,
                syncCommanded: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await PublishRunJsonEvidenceAsync(
            "qhy-mount-coordinate-sync-completed",
            "One-time QHY/PL3 mount coordinate recovery verified",
            new
            {
                intentPath,
                authority = "Fresh current-run QHY/PL3 formal WCS",
                before = new { raDegrees = before.RADegrees, decDegrees = before.Dec, epoch = before.Epoch.ToString() },
                after = new { raDegrees = after.RADegrees, decDegrees = after.Dec, epoch = after.Epoch.ToString() },
                qhyTruth = new { raDegrees = qhyCoordinates.RADegrees, decDegrees = qhyCoordinates.Dec, epoch = qhyCoordinates.Epoch.ToString() },
                qhyTruthJ2000 = new
                {
                    raDegrees = qhyTruthJ2000.RADegrees,
                    decDegrees = qhyTruthJ2000.Dec,
                    epoch = qhyTruthJ2000.Epoch.ToString(),
                },
                qhySyncCommand = new
                {
                    raDegrees = qhySyncCommandCoordinates.RADegrees,
                    decDegrees = qhySyncCommandCoordinates.Dec,
                    epoch = qhySyncCommandCoordinates.Epoch.ToString(),
                },
                qhyTruthAtReadbackEpoch = new
                {
                    raDegrees = qhyTruthAtReadbackEpoch.RADegrees,
                    decDegrees = qhyTruthAtReadbackEpoch.Dec,
                    epoch = qhyTruthAtReadbackEpoch.Epoch.ToString(),
                },
                pierSide = afterPierSide,
                residualBeforeArcseconds = residualBefore,
                residualAfterArcseconds = residualAfter,
                mountSlewCommandCount = 0,
                verified = true,
            },
            accepted.FitsPath,
            cancellationToken).ConfigureAwait(false);
        var catalogueSlew = await SlewToCatalogTargetAfterQhyCoordinateSyncAsync(
            context,
            accepted,
            qhyCoordinates,
            cancellationToken).ConfigureAwait(false);
        context.Set("qhyMountCoordinateSyncPerformed", true);
        context.Set("qhyMountCoordinateSyncResidualBeforeArcseconds", residualBefore);
        context.Set("qhyMountCoordinateSyncResidualAfterArcseconds", residualAfter);
        return new QhyMountCoordinateRecoveryResult(
            "FreshQhyWcsSyncedAndVerified",
            true,
            true,
            residualBefore,
            residualAfter,
            $"差 {residualBefore / 3600d:F3}°，已用 QHY/PL3 真值执行一次 Sync、以 {residualAfter:F2}″ 读回验证，并重新执行目录转向（实报残差 {catalogueSlew.CommandResidualArcseconds:F2}″；光学到位仍待 fresh G3）");
    }

    private async Task<QhyMountCoordinateRecoveryResult> ContinueWithFreshQhyHintAfterUnverifiedSyncAsync(
        ObservationContext context,
        QhyFrameRecord accepted,
        PlateSolveEvidence qhySolve,
        Coordinates qhyCoordinates,
        string failureCode,
        string failureMessage,
        string expectedPierSide,
        double residualBeforeArcseconds,
        double? residualAfterArcseconds,
        bool syncCommanded,
        CancellationToken cancellationToken)
    {
        var currentInfo = telescopeMediator.GetInfo();
        var currentPierSide = currentInfo.SideOfPier.ToString();
        if (!currentInfo.Connected || currentInfo.AtPark || currentInfo.Slewing ||
            !string.Equals(expectedPierSide, currentPierSide, StringComparison.OrdinalIgnoreCase))
        {
            throw new PhysicalActionGateException(GateResult.Unknown(
                failureCode,
                $"{failureMessage} Hint-only continuation is also withheld because connected={currentInfo.Connected}, parked={currentInfo.AtPark}, slewing={currentInfo.Slewing}, pier={expectedPierSide}->{currentPierSide}."));
        }

        var evidencePath = await PublishRunJsonEvidenceAsync(
            "qhy-mount-coordinate-sync-degraded-to-hint",
            "Unverified stationary QHY mount Sync degraded to fresh optical solve hint",
            new
            {
                context.Plan.ObservationRunId,
                failureCode,
                failureMessage,
                qhyFrame = accepted.FitsPath,
                qhyFrameSha256 = accepted.Sha256,
                qhySolveEvidence = qhySolve.EvidencePath,
                qhySolveEvidenceSha256 = qhySolve.EvidenceSha256,
                qhyTruth = new
                {
                    raDegrees = qhyCoordinates.RADegrees,
                    decDegrees = qhyCoordinates.Dec,
                    epoch = qhyCoordinates.Epoch.ToString(),
                },
                residualBeforeArcseconds,
                residualAfterArcseconds,
                syncCommanded,
                syncVerified = false,
                additionalSyncAuthorized = false,
                catalogueSlewAuthorized = false,
                mountMotionAuthority = false,
                nextAction = "Use the same fresh immutable QHY/PL3 WCS only as the G3 solver hint; require fresh G3 evidence before any motion.",
            },
            accepted.FitsPath,
            cancellationToken).ConfigureAwait(false);
        context.Set("qhyMountCoordinateSyncDegradedEvidencePath", evidencePath);
        context.Set("qhyMountCoordinateSyncFailureCode", failureCode);
        context.Set("qhyMountCoordinateSyncPerformed", syncCommanded);
        return new QhyMountCoordinateRecoveryResult(
            "FreshQhyWcsAfterUnverifiedSyncHintOnly",
            syncCommanded,
            false,
            residualBeforeArcseconds,
            residualAfterArcseconds,
            $"{failureCode}: one stationary Sync attempt was not verified; it will not be repeated and no catalogue slew is authorized. Fresh QHY/PL3 remains G3 solve-hint-only.");
    }

    private async Task<QhyPostSyncCatalogSlewResult> SlewToCatalogTargetAfterQhyCoordinateSyncAsync(
        ObservationContext context,
        QhyFrameRecord accepted,
        Coordinates qhyCoordinates,
        CancellationToken cancellationToken)
    {
        var target = TargetCoordinates(context.Plan);
        var horizon = ValidateCommandCoordinateHorizon(
            context,
            target,
            "catalogue slew after QHY WCS coordinate Sync");
        if (horizon.Disposition != GateDisposition.Passed)
        {
            throw new PhysicalActionGateException(horizon);
        }
        await RequireImmediatePhysicalActionGatesAsync(context, cancellationToken).ConfigureAwait(false);
        var before = telescopeMediator.GetCurrentPosition();
        var intentPath = await PublishRunJsonEvidenceAsync(
            "qhy-post-sync-catalog-slew-intent",
            "Catalogue slew reissued after one-time QHY WCS coordinate recovery",
            new
            {
                authority = "Fresh QHY/PL3 WCS-verified mount coordinate",
                qhyFrame = accepted.FitsPath,
                qhyTruth = new { raDegrees = qhyCoordinates.RADegrees, decDegrees = qhyCoordinates.Dec },
                before = new { raDegrees = before.RADegrees, decDegrees = before.Dec, epoch = before.Epoch.ToString() },
                target = new { raDegrees = target.RADegrees, decDegrees = target.Dec, epoch = target.Epoch.ToString() },
                predictedSlewArcseconds = AngularSeparationArcseconds(before, target),
                horizonGate = new { disposition = horizon.Disposition.ToString(), horizon.Code, horizon.Message, horizon.Metrics },
                commandCount = 1,
                reason = "The original catalogue slew used a grossly wrong no-home mount coordinate; Sync repaired coordinates without motion, so the catalogue slew must be issued once again.",
            },
            accepted.FitsPath,
            cancellationToken).ConfigureAwait(false);
        Report("QHY/PL3 已校正无机械零位赤道仪坐标；重新执行一次目录转向，随后由 fresh G3 WCS 验证光学到位");
        if (!await telescopeMediator.SlewToCoordinatesAsync(target, cancellationToken).ConfigureAwait(false))
        {
            throw new PhysicalActionGateException(GateResult.Unknown(
                "QHY_POST_SYNC_CATALOG_SLEW_REJECTED",
                "N.I.N.A. rejected the single catalogue slew after QHY WCS coordinate Sync."));
        }
        await telescopeMediator.WaitForSlew(cancellationToken).ConfigureAwait(false);
        var immediatelyAfter = telescopeMediator.GetCurrentPosition();
        var expectedPierSide = telescopeMediator.GetInfo().SideOfPier.ToString();
        var stability = await WaitForG3PostSlewStabilityAsync(
            context,
            target,
            immediatelyAfter,
            expectedPierSide,
            immediatelyAfter.Epoch.ToString(),
            "catalogue slew after QHY WCS coordinate Sync",
            configuration.G3.WcsFreshSolveAuthorizationResidualArcseconds,
            cancellationToken).ConfigureAwait(false);
        if (stability.Gate.Disposition != GateDisposition.Passed)
        {
            throw new PhysicalActionGateException(stability.Gate);
        }
        var settledReported = stability.Reported
            ?? throw new PhysicalActionGateException(GateResult.Unknown(
                "QHY_POST_SYNC_CATALOG_SLEW_READBACK_MISSING",
                "The post-Sync catalogue slew passed without a reported mount coordinate; fresh G3 acquisition is withheld."));
        await PublishRunJsonEvidenceAsync(
            "qhy-post-sync-catalog-slew-completed",
            "Catalogue slew after QHY WCS coordinate recovery completed; fresh G3 verification required",
            new
            {
                intentPath,
                target = new { raDegrees = target.RADegrees, decDegrees = target.Dec, epoch = target.Epoch.ToString() },
                reported = new
                {
                    raDegrees = settledReported.RADegrees,
                    decDegrees = settledReported.Dec,
                    epoch = settledReported.Epoch.ToString(),
                },
                stability.ReportedDriftArcseconds,
                commandResidualArcseconds = stability.CommandResidualArcseconds,
                commandCount = 1,
                opticalArrivalAuthority = "next fresh G3 WCS",
            },
            accepted.FitsPath,
            cancellationToken).ConfigureAwait(false);
        context.Set("qhyPostSyncCatalogSlewPerformed", true);
        return new QhyPostSyncCatalogSlewResult(stability.CommandResidualArcseconds);
    }

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
        // Paired QHY/G3 WCS is optional calibration telemetry, never a
        // prerequisite for G3 centering or PHD2 slit placement. Collect at most
        // one pair per observation run. Retrying it after every successful G3
        // solve put a slow QHY exposure/solve inside the WCS correction loop and
        // allowed an already-near-slit target to drift before PHD2 takeover.
        if (string.Equals(qhyG3FastPairCollectionRunId, context.Plan.ObservationRunId, StringComparison.Ordinal))
        {
            qhyG3FastPairOutcome = "AlreadyAttemptedForRun";
            return;
        }
        qhyG3FastPairCollectionRunId = context.Plan.ObservationRunId;
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
            string? automaticCalibrationPath = null;
            string? automaticCalibrationWarning = null;
            try
            {
                automaticCalibrationPath = await PersistQhyG3CandidateCalibrationAsync(
                    build.Candidate,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The immutable run evidence above is already complete.  A
                // machine-local convenience index failure is a warning, never
                // a reason to discard the valid pair or stop acquisition.
                automaticCalibrationWarning = ex.Message;
                await WriteAuditBestEffortAsync("qhy-g3-automatic-calibration-index-warning", new
                {
                    build.Candidate.CalibrationId,
                    build.Candidate.CandidateSha256,
                    warning = ex.Message,
                    candidateEvidencePath = latestQhyG3TransferCandidateEvidencePath,
                    motionAuthority = false,
                }).ConfigureAwait(false);
            }
            context.Set("qhyG3FastPairOutcome", qhyG3FastPairOutcome);
            context.Set("qhyG3FastPairCandidateId", build.Candidate.CalibrationId);
            context.Set("qhyG3FastPairCandidateSha256", build.Candidate.CandidateSha256);
            context.Set("qhyG3FastPairEvidencePath", latestQhyG3TransferCandidateEvidencePath);
            if (automaticCalibrationPath is not null)
                context.Set("qhyG3AutomaticCalibrationPath", automaticCalibrationPath);
            if (automaticCalibrationWarning is not null)
                context.Set("qhyG3AutomaticCalibrationWarning", automaticCalibrationWarning);
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
                automaticCalibrationPath,
                automaticCalibrationWarning,
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

    /// <summary>
    /// Keeps every valid same-pointing solve pair in a machine-local,
    /// installation-scoped calibration archive and atomically advances a
    /// latest-candidate index.  This removes manual transcription of the two
    /// optical-axis measurement.  The index is data, not motion authority: a
    /// single sample remains Candidate and cannot be used as a slew command.
    /// </summary>
    private static async Task<string> PersistQhyG3CandidateCalibrationAsync(
        QhyToG3TransferCandidate candidate,
        CancellationToken cancellationToken)
    {
        var integrity = candidate.ValidateIntegrity();
        if (integrity.Count > 0)
        {
            throw new InvalidOperationException(
                "Cannot persist an invalid QHY/G3 automatic calibration candidate: " + string.Join(" ", integrity));
        }

        var fingerprintText = string.Join(
            "|",
            candidate.InstallationEpochId,
            candidate.TelescopeDeviceId,
            candidate.QhyCameraStableId,
            candidate.G3CameraStableId,
            candidate.QhyOpticalTrainId,
            candidate.G3OpticalTrainId,
            candidate.PierSide,
            candidate.Model.ModelKind,
            candidate.Model.ProjectionId);
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintText))).ToLowerInvariant();
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UVEX-ADV",
            "calibration",
            "qhy-g3",
            fingerprint[..24]);
        Directory.CreateDirectory(directory);

        var immutablePath = Path.Combine(
            directory,
            $"candidate-{candidate.CreatedUtc.UtcDateTime:yyyyMMddTHHmmssfffZ}-{candidate.CandidateSha256[..24].ToLowerInvariant()}.json");
        var envelope = new
        {
            schemaVersion = 1,
            recordKind = "QhyToG3AutomaticCalibrationCandidate",
            hardwareFingerprintSha256 = fingerprint,
            updatedUtc = DateTimeOffset.UtcNow,
            lifecycle = candidate.Lifecycle.ToString(),
            motionAuthority = false,
            automaticCollection = true,
            candidate,
            interpretation = new
            {
                measuredQuantity = "G3 WCS centre minus QHY WCS centre at the same unchanged mount pointing",
                notMeasuredQuantity = "QHY/target or mount/target residual",
                activation = "Multi-sample independent verification remains required before any pre-positioning motion.",
            },
        };
        if (!File.Exists(immutablePath))
        {
            await WriteJsonAtomicallyAsync(immutablePath, envelope, overwrite: false, cancellationToken).ConfigureAwait(false);
        }

        var latestIndexPath = Path.Combine(directory, "latest-candidate.json");
        await WriteJsonAtomicallyAsync(latestIndexPath, envelope, overwrite: true, cancellationToken).ConfigureAwait(false);
        return immutablePath;
    }

    private static async Task WriteJsonAtomicallyAsync(
        string path,
        object payload,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1 << 16,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    payload,
                    EvidenceJsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
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

internal sealed record G3PlateSolveHintSelection(
    Coordinates Coordinates,
    string Authority,
    string Reason);

internal sealed record QhyMountCoordinateRecoveryResult(
    string Outcome,
    bool SyncCommanded,
    bool SyncVerified,
    double MountResidualBeforeArcseconds,
    double? MountResidualAfterArcseconds,
    string Message);

internal sealed record QhyPostSyncCatalogSlewResult(
    double CommandResidualArcseconds);
