using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed record Phd2DistanceGuardEvidence(Phd2DistanceGuardWitness Native,
    Phd2PostLockGuidingObservation Proof, IReadOnlyList<Phd2GuidingResidualState> Frames,
    IReadOnlyList<Phd2DistanceGuardWitness>? AdditionalVetoes = null);

// There is intentionally no command surface here. One invocation per already charged
// outbound readback. Await native recovery; then measure the field from the existing stream.
internal static class Phd2DistanceGuardRecovery
{
    internal static DateTimeOffset Deadline(DateTimeOffset stageEnd, DateTimeOffset motionStart,
        Phd2LockShiftLimits limits, int reservedReturnStages, DateTimeOffset? targetEnd, DateTimeOffset? nightEnd) =>
        new[] { stageEnd, motionStart + limits.MaximumElapsed - limits.MaximumStageDuration * reservedReturnStages,
            targetEnd ?? DateTimeOffset.MaxValue, nightEnd ?? DateTimeOffset.MaxValue }.Min();

    internal static async Task<Phd2DistanceGuardEvidence?> ObserveAsync(Phd2SlitPlacementSession session,
        Phd2SlitPlacementCommissioning preset, Phd2IdentityValidation identity, Phd2ExactLockPositionResult exact,
        Phd2StateSnapshot before, Phd2GuidingFrameLostException loss, DateTimeOffset deadline,
        Func<Phd2GuideStep,DateTimeOffset,CancellationToken,Task<Phd2DistanceGuardWitness?>> readWitness, Func<Phd2StateSnapshot> snapshot,
        Func<CancellationToken,Task> safety, Func<CancellationToken,Task<bool>> readBinding,
        Func<CancellationToken,Task<IReadOnlyList<Phd2GuidingResidualState>>> readFrames,
        CancellationToken token, TimeProvider? time = null)
    {
        var clock = time ?? TimeProvider.System;
        token.ThrowIfCancellationRequested();
        var tolerance = preset.LockVerificationTolerancePixels;
        var reference = session.LastMeasurement.Measurement;
        if (loss.LostEventUtc is not { } lostUtc || loss.LostEventSequence is not { } seq ||
            loss.LostFrame is not { ErrorCode: 8, Frame: > 0, Snr: { } snr, HfdPixels: > 0 } dropped ||
            !double.IsFinite(snr) || !(snr >= Math.Max(15, preset.MinimumGuideSignalToNoise)) ||
            !reference.GuidePositionMeasuredInFrame || !reference.TargetIdentityConfirmed ||
            reference.TargetPositionAuthority is not (Phd2TargetPositionAuthority.DetectedTargetCentroid or
                Phd2TargetPositionAuthority.CatalogWcsIdentityWithSaturatedTopologyCentroid) ||
            session.GuideMode != Phd2SlitGuideMode.OffSlitGuideStar || !session.Quality.IsLockShiftAuthority ||
            !identity.IsValid || !exact.Exact || exact.RegistryProfileMutated || exact.AutomaticRetryAllowed ||
            !(exact.VerificationErrorPixels >= 0 && exact.VerificationErrorPixels <= tolerance) ||
            !Near(exact.Verified, exact.Requested, tolerance) || before.ConnectionEpoch != session.ConnectionEpoch ||
            before.GuideEpoch != session.GuideEpoch || seq <= before.EventSequence ||
            before.AppState != Phd2AppState.Guiding || !Bound(before) || lostUtc < exact.CompletedUtc ||
            lostUtc > clock.GetUtcNow()) return null;

        bool Bound(Phd2StateSnapshot s) => s.IsConnected && !s.AutomationPaused && !s.Phd2Paused &&
            s.PendingSettleOperationId is null && s.GuideOutput?.Failed != true && s.ConnectionEpoch == session.ConnectionEpoch &&
            s.CalibrationChangeSequence == session.Calibration.CalibrationChangeSequence &&
            s.Profile == identity.Profile && s.Equipment == identity.Equipment &&
            s.LastConfigurationChange == before.LastConfigurationChange &&
            Near(s.SelectedStar, session.SelectedGuide, tolerance) && Near(s.LockPosition, exact.Verified, tolerance);
        var certifiedSeq = seq;
        var certifiedEpoch = before.GuideEpoch + 1;
        var certifiedFrame = dropped.Frame!.Value;
        var additional = new List<Phd2DistanceGuardWitness>();
        bool SameLoss(Phd2StateSnapshot s) => Bound(s) && s.LastPreviewStarMissSequence == certifiedSeq;
        bool Transition(Phd2StateSnapshot s) =>
            s.RecentGuideEpochChanges.Count >= 2 && s.RecentGuideEpochChanges[^2] is { Cause: "event:StarLost" } last &&
            last.Epoch == certifiedEpoch && last.EventSequence == certifiedSeq &&
            s.RecentGuideEpochChanges[^1] is { Cause: "event:GuideStep" or "event:AppState",
                PreviousState: Phd2AppState.LostLock, State: Phd2AppState.Guiding } found &&
            found.PreviousEpoch == certifiedEpoch && found.Epoch == certifiedEpoch + 1 && s.GuideEpoch == found.Epoch &&
            found.EventSequence > certifiedSeq && found.ObservedUtc >= last.ObservedUtc;
        bool Resumed(Phd2StateSnapshot s) => SameLoss(s) && Transition(s) && s.AppState == Phd2AppState.Guiding &&
            s.GuideOutput is { Failed:false, ConsecutiveMissingOutputs:0 } o && o.UpdatedUtc >= s.RecentGuideEpochChanges[^2].ObservedUtc &&
            s.LastGuideStep?.Frame == o.LastFrame && o.LastFrame > certifiedFrame;
        var until = new[] { deadline, lostUtc.AddSeconds(8) }.Min();
        if (clock.GetUtcNow() >= until || !SameLoss(loss.Snapshot) ||
            !loss.Snapshot.RecentGuideEpochChanges.Any(c => c is { Cause:"event:StarLost", PreviousState:Phd2AppState.Guiding,
                State:Phd2AppState.LostLock } && c.EventSequence == seq && c.PreviousEpoch == before.GuideEpoch && c.Epoch == certifiedEpoch)) return null;
        // Even a slow safety/owner read or another shutter period cannot consume the reserved return.
        var remaining = deadline - clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero) return null;
        using var timer = new CancellationTokenSource(remaining, clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token);
        var ct = bounded.Token;
        try
        {
            var waitRemaining = until - clock.GetUtcNow();
            if (waitRemaining <= TimeSpan.Zero) return null;
            using var waitTimer = new CancellationTokenSource(waitRemaining, clock);
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, waitTimer.Token);
            Phd2StateSnapshot current;
            Phd2DistanceGuardWitness witness;
            try
            {
                if (await readWitness(dropped,lostUtc,waiting.Token).WaitAsync(waiting.Token).ConfigureAwait(false) is not { } first ||
                    first.Frame != dropped.Frame) return null;
                witness = first;
                while (true)
                {
                    await safety(waiting.Token).ConfigureAwait(false);
                    current = snapshot();
                    if (clock.GetUtcNow() >= until) return null;
                    if (current.LastPreviewStarMissSequence != certifiedSeq)
                    {
                        // Native WAITING may veto several consecutive frames for the same
                        // jump. Never extend the original timer, bridge a missed event or
                        // accept a second loss after Guiding was re-established.
                        var r = current.LastNativeStarLoss;
                        if (!Bound(current) || additional.Count >= 6 || r is null ||
                            r.Sequence != current.LastPreviewStarMissSequence || r.PreviousMissSequence != certifiedSeq ||
                            r.ReceivedUtc < lostUtc || r.ReceivedUtc >= until || r.ReceivedUtc > clock.GetUtcNow() ||
                            r.Frame is not { ErrorCode:8, Frame:{ } f, Snr:{ } s, HfdPixels:>0 } ||
                            f != certifiedFrame+1 || !double.IsFinite(s) || !(s >= Math.Max(15,preset.MinimumGuideSignalToNoise)) ||
                            !current.RecentGuideEpochChanges.Any(c => c is { Cause:"event:StarLost",
                                PreviousState:Phd2AppState.LostLock, State:Phd2AppState.LostLock } &&
                                c.EventSequence == r.Sequence && c.PreviousEpoch == certifiedEpoch && c.Epoch == certifiedEpoch+1) ||
                            await readWitness(r.Frame,r.ReceivedUtc,waiting.Token).WaitAsync(waiting.Token).ConfigureAwait(false)
                                is not { } repeated || repeated.Frame != f ||
                            clock.GetUtcNow() >= until) return null;
                        additional.Add(repeated);
                        certifiedSeq=r.Sequence; certifiedEpoch++; certifiedFrame=f;
                    }
                    if (Resumed(current)) break;
                    bool awaitingFirstOutput = current.AppState == Phd2AppState.Guiding && current.GuideOutput is null &&
                        Transition(current);
                    bool stillLost = current.AppState == Phd2AppState.LostLock &&
                        current.LastGuideEpochChange is { Cause: "event:StarLost" } change &&
                        change.Epoch == certifiedEpoch && change.EventSequence == certifiedSeq;
                    if (!SameLoss(current) || !(stillLost || awaitingFirstOutput)) return null;
                    await Task.Delay(TimeSpan.FromMilliseconds(200), clock, waiting.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && waitTimer.IsCancellationRequested) { return null; }
            if (!await readBinding(ct).ConfigureAwait(false) || !Resumed(current = snapshot())) return null;
            var proof = new Phd2PostLockGuidingObservation("native-distance-" + Guid.NewGuid().ToString("N"),
                current.ConnectionEpoch, current.GuideEpoch, current.EventSequence, exact.Verified, tolerance,
                clock.GetUtcNow(), YieldedToFreshResiduals: true, MeasuredContinuation: true);
            var frames = await readFrames(ct).ConfigureAwait(false);
            if (!await readBinding(ct).ConfigureAwait(false)) return null;
            await safety(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            current = snapshot();
            if (clock.GetUtcNow() >= deadline || !Resumed(current) ||
                !Phd2PlacementGuideWindowPolicy.HasUsableMeasuredWindow(frames.Select(f => f.Measurement).ToArray(),
                    frames.Select(f => Phd2DistanceGuardWitness.Distance(f.Measurement.GuideStar, exact.Verified)).ToArray(),
                    proof.IsCurrent(current), current.GuideOutput is { Failed: false, ConsecutiveMissingOutputs: 0 }) ||
                !frames.All(f => f.NativePrimaryMatched && f.Frame.NativeGuideStep is { Frame: { } number, ErrorCode: null or 0 or 1 } &&
                    number == f.Frame.TriggerGuideFrame && f.Frame.PrimaryStarImage?.Frame == number &&
                    Near(f.Frame.NativeLockPosition, exact.Verified, tolerance) &&
                    f.Measurement.TopologyFingerprintSha256 == reference.TopologyFingerprintSha256 &&
                    string.Equals(f.Measurement.FrameSha256, f.Frame.Sha256, StringComparison.OrdinalIgnoreCase) &&
                    f.Measurement.TargetPositionAuthority is Phd2TargetPositionAuthority.DetectedTargetCentroid or
                        Phd2TargetPositionAuthority.CatalogWcsIdentityWithSaturatedTopologyCentroid &&
                    Near(f.Measurement.RecognizedSlitAcquisitionPoint, reference.RecognizedSlitAcquisitionPoint, 3) &&
                    Near(Vector(f.Measurement), Vector(reference), 6))) return null;
            return new(witness, proof.AcceptResiduals(frames.Select(f => f.Frame).ToArray(), current), frames, additional);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timer.IsCancellationRequested) { return null; }
        // Do not relabel actual device/output/cancellation faults as an optical rejection.
        catch (Exception e) when (e.GetType() == typeof(Phd2Exception) || e is TargetQualityEvidenceException) { return null; }
    }

    private static Phd2Point Vector(Phd2SlitFieldMeasurement m) => new(m.GuideStar.X - m.TargetCentroid.X, m.GuideStar.Y - m.TargetCentroid.Y);
    private static bool Near(Phd2Point? a, Phd2Point b, double tolerance) =>
        a is not null && Phd2DistanceGuardWitness.Distance(a,b) <= tolerance;
}
