using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal enum Phd2LockShiftPendingPhase
{
    StageIntent = 0,
    AwaitingOperationBoundSettle = 1,
    AwaitingFreshResidual = 2,
    ReturnRequired = 3,
    SettledBudgetLedger = 4,
    // An explicitly cancelled observation was abandoned after a verified home
    // boundary or disjoint-target acquisition. Not return/scientific success.
    RetiredCancelledObservation = 5,
}

internal sealed record Phd2ForeignRecoveryEndpointProof(
    GateResult Gate,
    Phd2Point? ProvenEndpoint,
    double CurrentRequestedLockErrorPixels,
    bool EndpointPhaseProven);

internal sealed record Phd2RecoveryFieldTranslationProof(
    GateResult Gate,
    Phd2Point? ObservedTranslation,
    int MatchedStars,
    double ResidualRmsPixels,
    double ExpectedTranslationErrorPixels);

internal sealed record Phd2RecoveryReturnVerification(
    GateResult Gate,
    bool NoMotionReturn,
    bool ExactOriginLockVerified,
    bool TargetVectorVerified,
    bool FieldTranslationVerified,
    bool SlitVerified,
    bool FreshSlitReacquisitionRequired,
    double ReturnDistancePixels,
    double TargetVectorErrorPixels);

/// <summary>
/// Determines whether an unfinished foreign-run lock ledger contains an
/// unambiguous, readback-verified physical endpoint. Absolute target/slit
/// pixels are intentionally excluded because they belong to the old target.
/// </summary>
internal static class Phd2ZeroVectorPierRecoveryPolicy
{
    // This is permission to collect fresh verification, never permission to
    // settle a ledger or reinterpret a nonzero detector vector across a flip.
    public static bool CanVerifyWithoutMotion(Phd2LockShiftPendingState state, Phd2SensorTopology current)
    {
        if (state.Phase != Phd2LockShiftPendingPhase.ReturnRequired ||
            !double.IsFinite(state.OriginLockX) || !double.IsFinite(state.OriginLockY) ||
            state.CurrentLockX != state.OriginLockX || state.CurrentLockY != state.OriginLockY ||
            state.RequestedLockX != state.OriginLockX || state.RequestedLockY != state.OriginLockY)
            return false;
        var otherPier = current.PierSide switch
        {
            "pierEast" => "pierWest",
            "pierWest" => "pierEast",
            _ => null,
        };
        return otherPier is not null && string.Equals(
            (current with { PierSide = otherPier }).ComputeFingerprintSha256(),
            state.TopologyFingerprintSha256, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class Phd2ForeignRecoveryEndpointPolicy
{
    public static Phd2ForeignRecoveryEndpointProof Evaluate(
        Phd2LockShiftPendingState state,
        double proofTolerancePixels)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!double.IsFinite(proofTolerancePixels) || proofTolerancePixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(proofTolerancePixels));

        var current = new Phd2Point(state.CurrentLockX, state.CurrentLockY);
        var requested = new Phd2Point(state.RequestedLockX, state.RequestedLockY);
        var dx = current.X - requested.X;
        var dy = current.Y - requested.Y;
        var error = Math.Sqrt(dx * dx + dy * dy);
        // ReturnRequired is also recoverable when CurrentLock still agrees
        // with RequestedLock. Every ambiguous pre-dispatch/dispatch failure
        // retains different values; equality means a verified post-dispatch
        // endpoint (or a no-motion endpoint) was durably retained. This also
        // covers a recovery planner that checked-stopped after its preamble.
        var endpointPhaseProven = state.Phase is
            Phd2LockShiftPendingPhase.AwaitingOperationBoundSettle or
            Phd2LockShiftPendingPhase.AwaitingFreshResidual or
            Phd2LockShiftPendingPhase.ReturnRequired;
        if (!endpointPhaseProven || !double.IsFinite(error) || error > proofTolerancePixels)
        {
            return new Phd2ForeignRecoveryEndpointProof(
                GateResult.Unknown(
                    "PHD2_LOCK_RECOVERY_FOREIGN_ENDPOINT_UNPROVEN",
                    $"Foreign PHD2 lineage {state.LineageId} is in phase {state.Phase} with current/requested lock disagreement {error:F3}px (limit {proofTolerancePixels:F3}px); its old physical endpoint is not uniquely proven, so no return command was sent."),
                null,
                error,
                endpointPhaseProven);
        }

        return new Phd2ForeignRecoveryEndpointProof(
            GateResult.Pass(
                "PHD2_LOCK_RECOVERY_FOREIGN_ENDPOINT_PROVEN",
                $"Foreign PHD2 lineage {state.LineageId} retained a verified post-dispatch endpoint: CurrentLock and RequestedLock agree within {error:F3}px."),
            current,
            error,
            true);
    }
}

/// <summary>
/// Measures the common translation of ordinary, unsaturated stars between two
/// fresh G3 frames.  The expected PHD2 lock delta is used only as the bounded
/// correspondence search centre; a minimum three-star consensus supplies the
/// independent optical response proof.  Bright target structure and the
/// physical slit are excluded from the match.
/// </summary>
internal static class Phd2RecoveryFieldTranslationPolicy
{
    private const int MaximumStarsPerFrame = 128;
    private const int MinimumMatchedStars = 3;
    private const double TargetExclusionRadiusPixels = 64;
    private const double SlitExclusionRadiusPixels = 16;

    public static Phd2RecoveryFieldTranslationProof Evaluate(
        IReadOnlyList<StarCandidate> before,
        IReadOnlyList<StarCandidate> after,
        Phd2Point beforeTarget,
        Phd2Point afterTarget,
        Phd2Point beforeSlit,
        Phd2Point afterSlit,
        Phd2Point expectedTranslation,
        double proofTolerancePixels)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(beforeTarget);
        ArgumentNullException.ThrowIfNull(afterTarget);
        ArgumentNullException.ThrowIfNull(beforeSlit);
        ArgumentNullException.ThrowIfNull(afterSlit);
        ArgumentNullException.ThrowIfNull(expectedTranslation);
        if (!double.IsFinite(proofTolerancePixels) || proofTolerancePixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(proofTolerancePixels));

        var searchRadius = Math.Max(1.5, proofTolerancePixels * 1.5);
        var consensusRadius = Math.Max(1.25, proofTolerancePixels);
        var beforeStars = Eligible(before, beforeTarget, beforeSlit).Take(MaximumStarsPerFrame).ToArray();
        var afterStars = Eligible(after, afterTarget, afterSlit).Take(MaximumStarsPerFrame).ToArray();
        var proposed = new List<(int AfterIndex, double PredictionError, Phd2Point Delta)>();
        foreach (var source in beforeStars)
        {
            var predictedX = source.Centroid.X + expectedTranslation.X;
            var predictedY = source.Centroid.Y + expectedTranslation.Y;
            var ranked = afterStars
                .Select((candidate, index) => new
                {
                    Index = index,
                    Candidate = candidate,
                    Distance = Distance(candidate.Centroid.X, candidate.Centroid.Y, predictedX, predictedY),
                })
                .OrderBy(match => match.Distance)
                .Take(2)
                .ToArray();
            if (ranked.Length == 0 || ranked[0].Distance > searchRadius)
                continue;
            // A nearly tied neighbour is not a unique cross-frame track.
            if (ranked.Length > 1 && ranked[1].Distance - ranked[0].Distance < 0.5)
                continue;
            proposed.Add((
                ranked[0].Index,
                ranked[0].Distance,
                new Phd2Point(
                    ranked[0].Candidate.Centroid.X - source.Centroid.X,
                    ranked[0].Candidate.Centroid.Y - source.Centroid.Y)));
        }

        // One destination detection may not prove two different source tracks.
        var unique = proposed
            .GroupBy(match => match.AfterIndex)
            .Select(group => group.OrderBy(match => match.PredictionError).First())
            .ToArray();
        if (unique.Length < MinimumMatchedStars)
            return Unavailable(
                "PHD2_LOCK_RETURN_FIELD_TRANSLATION_INSUFFICIENT",
                $"Only {unique.Length} unique ordinary-star track(s) matched the expected return; {MinimumMatchedStars} are required.",
                unique.Length);

        var median = new Phd2Point(
            Median(unique.Select(match => match.Delta.X)),
            Median(unique.Select(match => match.Delta.Y)));
        var inliers = unique
            .Where(match => Distance(match.Delta.X, match.Delta.Y, median.X, median.Y) <= consensusRadius)
            .ToArray();
        if (inliers.Length < MinimumMatchedStars)
            return Unavailable(
                "PHD2_LOCK_RETURN_FIELD_TRANSLATION_INCONSISTENT",
                $"Only {inliers.Length} ordinary-star track(s) formed a common-translation consensus; {MinimumMatchedStars} are required.",
                inliers.Length);

        median = new Phd2Point(
            Median(inliers.Select(match => match.Delta.X)),
            Median(inliers.Select(match => match.Delta.Y)));
        var rms = Math.Sqrt(inliers.Average(match =>
        {
            var residual = Distance(match.Delta.X, match.Delta.Y, median.X, median.Y);
            return residual * residual;
        }));
        var expectedError = Distance(
            median.X,
            median.Y,
            expectedTranslation.X,
            expectedTranslation.Y);
        var metrics = new Dictionary<string, double>
        {
            ["matchedStars"] = inliers.Length,
            ["translationX"] = median.X,
            ["translationY"] = median.Y,
            ["translationResidualRmsPixels"] = rms,
            ["expectedTranslationErrorPixels"] = expectedError,
        };
        if (rms > proofTolerancePixels || expectedError > proofTolerancePixels)
        {
            return new Phd2RecoveryFieldTranslationProof(
                GateResult.Unknown(
                    "PHD2_LOCK_RETURN_FIELD_TRANSLATION_MISMATCH",
                    $"The {inliers.Length}-star field translation was ({median.X:F3},{median.Y:F3})px with RMS {rms:F3}px; its error from the expected return is {expectedError:F3}px (limit {proofTolerancePixels:F3}px).",
                    metrics),
                median,
                inliers.Length,
                rms,
                expectedError);
        }

        return new Phd2RecoveryFieldTranslationProof(
            GateResult.Pass(
                "PHD2_LOCK_RETURN_FIELD_TRANSLATION_VERIFIED",
                $"{inliers.Length} ordinary stars independently verified the common G3 return translation ({median.X:F3},{median.Y:F3})px.",
                metrics),
            median,
            inliers.Length,
            rms,
            expectedError);
    }

    private static IEnumerable<StarCandidate> Eligible(
        IEnumerable<StarCandidate> candidates,
        Phd2Point target,
        Phd2Point slit)
    {
        return candidates.Where(candidate =>
            double.IsFinite(candidate.Centroid.X) &&
            double.IsFinite(candidate.Centroid.Y) &&
            double.IsFinite(candidate.SignalToNoise) && candidate.SignalToNoise >= 5 &&
            double.IsFinite(candidate.FwhmPixels) && candidate.FwhmPixels > 0 &&
            double.IsFinite(candidate.Ellipticity) && candidate.Ellipticity <= 0.65 &&
            double.IsFinite(candidate.SaturatedFraction) && candidate.SaturatedFraction <= 0.02 &&
            double.IsFinite(candidate.EdgeDistancePixels) && candidate.EdgeDistancePixels >= 8 &&
            Distance(candidate.Centroid.X, candidate.Centroid.Y, target.X, target.Y) >= TargetExclusionRadiusPixels &&
            Distance(candidate.Centroid.X, candidate.Centroid.Y, slit.X, slit.Y) >= SlitExclusionRadiusPixels);
    }

    private static Phd2RecoveryFieldTranslationProof Unavailable(string code, string message, int matches) => new(
        GateResult.Unknown(code, message, new Dictionary<string, double> { ["matchedStars"] = matches }),
        null,
        matches,
        double.PositiveInfinity,
        double.PositiveInfinity);

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0) throw new InvalidOperationException("Median requires at least one value.");
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// Final gate for a return that has already reached the planner's origin and
/// therefore already owns exact-lock readback plus operation-bound settle.
/// A zero-distance return is reconciliation, not a new motion.  A non-zero
/// return needs either the strict target-vector check or independent common
/// field translation.  Saturated-target centroid wander is never allowed to
/// overrule a passing ordinary-star field registration.
/// </summary>
internal static class Phd2RecoveryReturnVerificationPolicy
{
    public static Phd2RecoveryReturnVerification Evaluate(
        bool foreignRun,
        Phd2Point returnDelta,
        Phd2Point measuredTargetDelta,
        double exactOriginLockErrorPixels,
        double originSlitErrorPixels,
        double sameEpochSlitStabilityErrorPixels,
        bool targetIdentityConfirmed,
        Phd2RecoveryFieldTranslationProof fieldTranslation,
        double proofTolerancePixels)
    {
        ArgumentNullException.ThrowIfNull(returnDelta);
        ArgumentNullException.ThrowIfNull(measuredTargetDelta);
        ArgumentNullException.ThrowIfNull(fieldTranslation);
        if (!double.IsFinite(proofTolerancePixels) || proofTolerancePixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(proofTolerancePixels));

        var returnDistance = Distance(returnDelta, new Phd2Point(0, 0));
        var targetVectorError = Distance(measuredTargetDelta, returnDelta);
        var noMotionReturn = returnDistance <= proofTolerancePixels;
        var exactOriginLockVerified = double.IsFinite(exactOriginLockErrorPixels) &&
            exactOriginLockErrorPixels <= proofTolerancePixels;
        var targetVectorVerified = double.IsFinite(targetVectorError) &&
            targetVectorError <= proofTolerancePixels;
        var fieldTranslationVerified = fieldTranslation.Gate.Disposition == GateDisposition.Passed;
        var slitError = foreignRun ? sameEpochSlitStabilityErrorPixels : originSlitErrorPixels;
        var slitVerified = double.IsFinite(slitError) && slitError <= proofTolerancePixels;
        var motionResponseVerified = noMotionReturn || targetVectorVerified || fieldTranslationVerified;
        // A foreign lineage only owes a return to its proven lock origin.  A
        // passing ordinary-star registration independently proves that return
        // even when the dark-slit detector's fitted locus wanders by a few
        // pixels between the two fields (bright/saturated targets can perturb
        // that fit).  In that case the slit disagreement is quarantined: the
        // old physical-motion debt may close, but the caller must discard the
        // field and reacquire fresh G3/PL3/slit evidence before authorizing any
        // new placement.  Same-run recovery still requires the strict slit
        // reproduction gate.
        var freshSlitReacquisitionRequired = foreignRun && fieldTranslationVerified && !slitVerified;
        var returnSlitEvidenceAccepted = slitVerified || freshSlitReacquisitionRequired;
        var passed = exactOriginLockVerified && targetIdentityConfirmed && returnSlitEvidenceAccepted && motionResponseVerified;
        var metrics = new Dictionary<string, double>
        {
            ["returnDistancePixels"] = returnDistance,
            ["exactOriginLockErrorPixels"] = exactOriginLockErrorPixels,
            ["targetVectorErrorPixels"] = targetVectorError,
            ["slitErrorPixels"] = slitError,
            ["fieldTranslationMatchedStars"] = fieldTranslation.MatchedStars,
            ["fieldTranslationErrorPixels"] = fieldTranslation.ExpectedTranslationErrorPixels,
            ["freshSlitReacquisitionRequired"] = freshSlitReacquisitionRequired ? 1 : 0,
        };
        var gate = passed
            ? GateResult.Pass(
                "PHD2_LOCK_RESTART_RETURN_FRESHLY_VERIFIED",
                freshSlitReacquisitionRequired
                    ? "Exact-lock readback and ordinary-star common translation verified the foreign return. The inconsistent slit fit was quarantined; fresh G3/PL3/slit reacquisition is mandatory before any new placement."
                    : noMotionReturn
                    ? "The exact runtime lock was already at the translated recovery origin; fresh target identity and stable physical-slit evidence completed reconciliation without another motion."
                    : fieldTranslationVerified && !targetVectorVerified
                        ? "Exact-lock readback, operation-bound settle and ordinary-star common translation verified the return; saturated-target centroid wander remained diagnostic only."
                        : "Exact-lock readback, operation-bound settle and fresh target/slit response verified the return.",
                metrics)
            : GateResult.Unknown(
                "PHD2_LOCK_RESTART_RETURN_RESIDUAL_MISMATCH",
                $"The runtime lock reached the recovery origin, but the fresh optical response was not proven: exact-lock error {exactOriginLockErrorPixels:F3}px, target-vector error {targetVectorError:F3}px, slit error {slitError:F3}px; field proof {fieldTranslation.Gate.Code} (limit {proofTolerancePixels:F3}px).",
                metrics);
        return new Phd2RecoveryReturnVerification(
            gate,
            noMotionReturn,
            exactOriginLockVerified,
            targetVectorVerified,
            fieldTranslationVerified,
            slitVerified,
            freshSlitReacquisitionRequired,
            returnDistance,
            targetVectorError);
    }

    private static double Distance(Phd2Point a, Phd2Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// Durable, run/config/topology/guide-epoch-bound ledger for PHD2 runtime lock
/// changes.  The origin is read from PHD2 immediately before this lineage; it
/// is not a stored optical offset and is never written to the PHD2 profile.
/// </summary>
internal sealed record Phd2LockShiftPendingState(
    int SchemaVersion,
    string ObservationRunId,
    string LineageId,
    string ActionConfigurationSha256,
    string CommissioningPresetSha256,
    string RecoveryContextSha256,
    string CalibrationQualityPolicyId,
    string CalibrationQualityPolicySha256,
    string TopologyFingerprintSha256,
    Phd2SlitGuideMode GuideMode,
    long ConnectionEpoch,
    long GuideEpoch,
    double OriginLockX,
    double OriginLockY,
    double CurrentLockX,
    double CurrentLockY,
    double RequestedLockX,
    double RequestedLockY,
    double MaximumStagePixels,
    double MaximumCumulativePixels,
    int MaximumAttempts,
    double MaximumElapsedSeconds,
    double CumulativeCommandedPixels,
    int AttemptsUsed,
    DateTimeOffset StartedUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    Phd2LockShiftPendingPhase Phase,
    string? LastAcceptedFrameSha256,
    string? LastFramePath,
    string? IntentEvidencePath,
    string? LastReason,
    double OriginTargetX = double.NaN,
    double OriginTargetY = double.NaN,
    double OriginSlitX = double.NaN,
    double OriginSlitY = double.NaN)
{
    public const int CurrentSchemaVersion = 3;

    /// <summary>
    /// Advances only the process-local guide epoch after a locally issued lock
    /// mutation or guide/settle operation has produced fresh readback proof.
    /// Durable motion debt, attempt/pixel budgets, lineage and start time are
    /// deliberately preserved.
    /// </summary>
    public Phd2LockShiftPendingState RebindAfterLocallyAttestedGuideEpoch(
        long connectionEpoch,
        long guideEpoch,
        Phd2Point verifiedLock,
        DateTimeOffset nowUtc,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(verifiedLock);
        if (connectionEpoch != ConnectionEpoch)
            throw new InvalidOperationException("A durable PHD2 lock lineage cannot be rebound across connection epochs.");
        if (guideEpoch < GuideEpoch)
            throw new InvalidOperationException("A durable PHD2 lock lineage cannot move backward to an older guide epoch.");
        if (!double.IsFinite(verifiedLock.X) || !double.IsFinite(verifiedLock.Y) ||
            verifiedLock.X < 0 || verifiedLock.Y < 0)
            throw new ArgumentOutOfRangeException(nameof(verifiedLock));
        if (nowUtc < UpdatedUtc)
            throw new ArgumentOutOfRangeException(nameof(nowUtc));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A guide-epoch rebind reason is required.", nameof(reason));

        return this with
        {
            GuideEpoch = guideEpoch,
            CurrentLockX = verifiedLock.X,
            CurrentLockY = verifiedLock.Y,
            UpdatedUtc = nowUtc,
            LastReason = reason,
        };
    }

    public Phd2LockShiftLedger ToPlannerLedger() => new(
        LineageId,
        new Phd2Point(OriginLockX, OriginLockY),
        new Phd2Point(CurrentLockX, CurrentLockY),
        AttemptsUsed,
        CumulativeCommandedPixels,
        StartedUtc,
        LastAcceptedFrameSha256);

    /// <summary>
    /// Creates an in-memory planner view for one explicitly initiated recovery
    /// episode. Passive downtime must not consume the bounded time available
    /// for a required return, while the durable lineage clock, attempt count,
    /// cumulative pixels and all physical endpoints remain unchanged.
    /// </summary>
    public Phd2LockShiftLedger ToRecoveryEpisodePlannerLedger(DateTimeOffset recoveryEpisodeStartedUtc)
    {
        if (recoveryEpisodeStartedUtc == default || recoveryEpisodeStartedUtc < StartedUtc)
            throw new ArgumentOutOfRangeException(nameof(recoveryEpisodeStartedUtc));
        return ToPlannerLedger() with { StartedUtc = recoveryEpisodeStartedUtc };
    }

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion) issues.Add($"PHD2 lock-shift pending schema must be {CurrentSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(ObservationRunId)) issues.Add("Observation run id is missing.");
        if (!Guid.TryParseExact(LineageId, "N", out _)) issues.Add("Lineage id must be a GUID in N format.");
        if (!IsSha(ActionConfigurationSha256)) issues.Add("Action-configuration SHA-256 is invalid.");
        if (!IsSha(CommissioningPresetSha256)) issues.Add("Commissioning-preset SHA-256 is invalid.");
        if (!IsSha(RecoveryContextSha256)) issues.Add("Recovery-context SHA-256 is invalid.");
        if (string.IsNullOrWhiteSpace(CalibrationQualityPolicyId)) issues.Add("Calibration-quality policy id is missing.");
        if (!IsSha(CalibrationQualityPolicySha256)) issues.Add("Calibration-quality policy SHA-256 is invalid.");
        if (!IsSha(TopologyFingerprintSha256)) issues.Add("Topology fingerprint SHA-256 is invalid.");
        if (!Enum.IsDefined(GuideMode) || !Enum.IsDefined(Phase)) issues.Add("Guide mode or pending phase is invalid.");
        if (ConnectionEpoch <= 0 || GuideEpoch <= 0) issues.Add("Connection and guide epochs must be positive.");
        ValidatePoint(issues, OriginLockX, OriginLockY, "origin lock");
        ValidatePoint(issues, OriginTargetX, OriginTargetY, "origin target");
        ValidatePoint(issues, OriginSlitX, OriginSlitY, "origin slit");
        ValidatePoint(issues, CurrentLockX, CurrentLockY, "current lock");
        ValidatePoint(issues, RequestedLockX, RequestedLockY, "requested lock");
        if (!double.IsFinite(MaximumStagePixels) || MaximumStagePixels <= 0) issues.Add("Maximum stage pixels is invalid.");
        if (!double.IsFinite(MaximumCumulativePixels) || MaximumCumulativePixels < MaximumStagePixels) issues.Add("Maximum cumulative pixels is invalid.");
        if (MaximumAttempts <= 0) issues.Add("Maximum attempts is invalid.");
        if (!double.IsFinite(MaximumElapsedSeconds) || MaximumElapsedSeconds <= 0) issues.Add("Maximum elapsed seconds is invalid.");
        if (!double.IsFinite(CumulativeCommandedPixels) || CumulativeCommandedPixels < 0 || CumulativeCommandedPixels > MaximumCumulativePixels + 1e-9)
            issues.Add("Consumed cumulative pixels is invalid.");
        if (AttemptsUsed < 0 || AttemptsUsed > MaximumAttempts) issues.Add("Consumed attempts is invalid.");
        if (StartedUtc == default || CreatedUtc < StartedUtc || UpdatedUtc < CreatedUtc) issues.Add("Pending timestamps are invalid.");
        if (LastAcceptedFrameSha256 is not null && !IsSha(LastAcceptedFrameSha256)) issues.Add("Last accepted frame SHA-256 is invalid.");
        return issues.AsReadOnly();
    }

    private static void ValidatePoint(ICollection<string> issues, double x, double y, string label)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) issues.Add($"{label} is non-finite.");
    }

    private static bool IsSha(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
}

internal sealed record Phd2LockShiftPendingLoadResult(Phd2LockShiftPendingState? State, string? Error);
internal sealed record Phd2LockShiftPendingFileResult(string Path, Phd2LockShiftPendingState? State, string? Error);

internal static class Phd2LockShiftBudgetHandoff
{
    private const double Epsilon = 1e-9;

    public static Phd2LockShiftPendingState CreateCurrentRunSettledCopy(
        Phd2LockShiftPendingState settledSource,
        string currentObservationRunId,
        string currentRecoveryContextSha256,
        DateTimeOffset nowUtc,
        string? currentTopologyFingerprintSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(settledSource);
        var issues = settledSource.Validate();
        if (issues.Count > 0) throw new InvalidOperationException(string.Join(" ", issues));
        if (settledSource.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger ||
            Distance(settledSource.CurrentLockX, settledSource.CurrentLockY, settledSource.OriginLockX, settledSource.OriginLockY) > Epsilon ||
            Distance(settledSource.RequestedLockX, settledSource.RequestedLockY, settledSource.OriginLockX, settledSource.OriginLockY) > Epsilon)
        {
            throw new InvalidOperationException("A current-run PHD2 budget handoff can only be created after a freshly verified return has settled at the durable origin.");
        }
        if (string.IsNullOrWhiteSpace(currentObservationRunId))
            throw new ArgumentException("Current observation run id is required.", nameof(currentObservationRunId));
        if (!IsSha(currentRecoveryContextSha256))
            throw new ArgumentException("Current recovery-context SHA-256 is invalid.", nameof(currentRecoveryContextSha256));
        if (currentTopologyFingerprintSha256 is not null && !IsSha(currentTopologyFingerprintSha256))
            throw new ArgumentException("Current topology fingerprint SHA-256 is invalid.", nameof(currentTopologyFingerprintSha256));
        if (nowUtc < settledSource.StartedUtc)
            throw new ArgumentOutOfRangeException(nameof(nowUtc), "Handoff time cannot precede the inherited budget clock.");

        return settledSource with
        {
            ObservationRunId = currentObservationRunId,
            RecoveryContextSha256 = currentRecoveryContextSha256,
            TopologyFingerprintSha256 = currentTopologyFingerprintSha256 ?? settledSource.TopologyFingerprintSha256,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc,
            IntentEvidencePath = null,
            LastReason = $"Settled PHD2 budget lineage handed off from run {settledSource.ObservationRunId}; lineage, limits, consumed attempts/pixels and earliest clock were preserved.",
        };
    }

    public static IReadOnlyList<string> ValidateCompletedHandoff(
        Phd2LockShiftPendingState source,
        Phd2LockShiftPendingState currentCopy,
        string expectedCurrentRunId,
        string expectedCurrentRecoveryContextSha256,
        string? expectedCurrentTopologyFingerprintSha256 = null)
    {
        var issues = new List<string>();
        issues.AddRange(source.Validate().Select(issue => $"source: {issue}"));
        issues.AddRange(currentCopy.Validate().Select(issue => $"current copy: {issue}"));
        if (!string.Equals(currentCopy.ObservationRunId, expectedCurrentRunId, StringComparison.Ordinal))
            issues.Add("current copy run id differs from the explicit run");
        if (!SameHash(currentCopy.RecoveryContextSha256, expectedCurrentRecoveryContextSha256))
            issues.Add("current copy recovery context differs from the explicit run");
        if (currentCopy.Phase != Phd2LockShiftPendingPhase.SettledBudgetLedger)
            issues.Add("current copy is not a settled budget ledger");
        if (!string.Equals(source.LineageId, currentCopy.LineageId, StringComparison.Ordinal))
            issues.Add("lineage id changed");
        if (!SameHash(source.ActionConfigurationSha256, currentCopy.ActionConfigurationSha256) ||
            !SameHash(source.CommissioningPresetSha256, currentCopy.CommissioningPresetSha256) ||
            !string.Equals(source.CalibrationQualityPolicyId, currentCopy.CalibrationQualityPolicyId, StringComparison.Ordinal) ||
            !SameHash(source.CalibrationQualityPolicySha256, currentCopy.CalibrationQualityPolicySha256))
            issues.Add("action, preset or policy binding changed");
        if (expectedCurrentTopologyFingerprintSha256 is null)
        {
            if (!SameHash(source.TopologyFingerprintSha256, currentCopy.TopologyFingerprintSha256))
                issues.Add("topology binding changed");
        }
        else if (!SameHash(currentCopy.TopologyFingerprintSha256, expectedCurrentTopologyFingerprintSha256))
        {
            issues.Add("current copy does not carry the explicitly resolved current topology binding");
        }
        if (source.GuideMode != currentCopy.GuideMode ||
            !Same(source.MaximumStagePixels, currentCopy.MaximumStagePixels) ||
            !Same(source.MaximumCumulativePixels, currentCopy.MaximumCumulativePixels) ||
            source.MaximumAttempts != currentCopy.MaximumAttempts ||
            !Same(source.MaximumElapsedSeconds, currentCopy.MaximumElapsedSeconds))
            issues.Add("guide mode or a bounded-motion limit changed");
        if (source.AttemptsUsed != currentCopy.AttemptsUsed ||
            !Same(source.CumulativeCommandedPixels, currentCopy.CumulativeCommandedPixels) ||
            source.StartedUtc != currentCopy.StartedUtc)
            issues.Add("consumed attempts, pixels or earliest budget clock changed");
        if (!Same(source.OriginLockX, currentCopy.OriginLockX) ||
            !Same(source.OriginLockY, currentCopy.OriginLockY) ||
            !Same(source.CurrentLockX, source.OriginLockX) ||
            !Same(source.CurrentLockY, source.OriginLockY) ||
            !Same(source.RequestedLockX, source.OriginLockX) ||
            !Same(source.RequestedLockY, source.OriginLockY) ||
            !Same(currentCopy.CurrentLockX, currentCopy.OriginLockX) ||
            !Same(currentCopy.CurrentLockY, currentCopy.OriginLockY) ||
            !Same(currentCopy.RequestedLockX, currentCopy.OriginLockX) ||
            !Same(currentCopy.RequestedLockY, currentCopy.OriginLockY))
            issues.Add("current copy does not attest the same settled runtime-lock origin");
        return issues.AsReadOnly();
    }

    private static bool Same(double left, double right) =>
        double.IsFinite(left) && double.IsFinite(right) && Math.Abs(left - right) <= Epsilon;

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool SameHash(string? left, string? right) =>
        string.Equals(NormalizeHash(left), NormalizeHash(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsSha(string? value) => NormalizeHash(value).Length == 64 && NormalizeHash(value).All(Uri.IsHexDigit);

    private static string NormalizeHash(string? value) =>
        (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
}

internal static class Phd2LockShiftPendingStore
{
    private sealed record Envelope(Phd2LockShiftPendingState State, string StateSha256);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task WriteAtomicAsync(
        string path,
        Phd2LockShiftPendingState state,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        var issues = state.Validate();
        if (issues.Count > 0) throw new InvalidOperationException($"Invalid PHD2 lock-shift pending state: {string.Join(" ", issues)}");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var envelope = new Envelope(state, Convert.ToHexString(SHA256.HashData(stateBytes)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static async Task<Phd2LockShiftPendingLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) return new Phd2LockShiftPendingLoadResult(null, null);
            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, JsonOptions);
            if (envelope?.State is null || string.IsNullOrWhiteSpace(envelope.StateSha256))
                return new Phd2LockShiftPendingLoadResult(null, "PHD2 lock-shift pending envelope is empty.");
            var stateBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.State, JsonOptions);
            var actual = Convert.ToHexString(SHA256.HashData(stateBytes));
            if (!string.Equals(actual, envelope.StateSha256, StringComparison.OrdinalIgnoreCase))
                return new Phd2LockShiftPendingLoadResult(null, "PHD2 lock-shift pending state SHA-256 mismatch.");
            var issues = envelope.State.Validate();
            return issues.Count == 0
                ? new Phd2LockShiftPendingLoadResult(envelope.State, null)
                : new Phd2LockShiftPendingLoadResult(null, string.Join(" ", issues));
        }
        catch (Exception ex)
        {
            return new Phd2LockShiftPendingLoadResult(null, ex.Message);
        }
    }

    public static async Task<IReadOnlyList<Phd2LockShiftPendingFileResult>> DiscoverAsync(
        string observationsRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationsRoot);
        var root = Path.GetFullPath(observationsRoot);
        if (!Directory.Exists(root)) return Array.Empty<Phd2LockShiftPendingFileResult>();
        var results = new List<Phd2LockShiftPendingFileResult>();
        foreach (var path in Directory
                     .EnumerateFiles(root, "phd2-lock-shift-pending.json", SearchOption.AllDirectories)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
            if (loaded.Error is null && loaded.State?.Phase == Phd2LockShiftPendingPhase.RetiredCancelledObservation)
                continue;
            results.Add(new Phd2LockShiftPendingFileResult(path, loaded.State, loaded.Error));
        }
        return results.AsReadOnly();
    }
}
