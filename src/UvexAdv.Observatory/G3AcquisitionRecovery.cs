using System.Security.Cryptography;
using System.Text.Json;

namespace UvexAdv.Observatory;

/// <summary>
/// Versioned, equipment-scoped exposure ladder used only to obtain a G3 WCS.
/// The values are commissioning data; this type deliberately contains no
/// camera-specific exposure defaults.
/// </summary>
public sealed record G3PlateSolveExposurePreset(
    int SchemaVersion,
    string PresetId,
    IReadOnlyList<int> ExposureMilliseconds)
{
    // Exposure-ladder payload semantics did not change when the independent
    // durable motion ledger moved to TAN projection schema 2.
    public const int CurrentSchemaVersion = 1;

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
        {
            issues.Add($"G3 plate-solve exposure preset schema must be {CurrentSchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(PresetId))
        {
            issues.Add("G3 plate-solve exposure preset id is missing.");
        }
        if (ExposureMilliseconds is null || ExposureMilliseconds.Count == 0)
        {
            issues.Add("G3 plate-solve exposure ladder must contain at least one exposure.");
            return issues.AsReadOnly();
        }

        var previous = 0;
        foreach (var exposure in ExposureMilliseconds)
        {
            if (exposure <= 0)
            {
                issues.Add("Every G3 plate-solve exposure must be positive.");
                break;
            }
            if (exposure <= previous)
            {
                issues.Add("G3 plate-solve exposures must be unique and strictly increasing.");
                break;
            }
            previous = exposure;
        }
        return issues.AsReadOnly();
    }
}

/// <summary>
/// Independent limits for a WCS-derived G3 recentering correction. These are
/// not the QHY coarse-centering limits and are not an optical-axis offset.
/// </summary>
public sealed record G3WcsCenteringLimits(
    int SchemaVersion,
    double MaximumSingleCorrectionArcseconds,
    double MaximumRadiusArcseconds,
    double MaximumCumulativeMotionArcseconds,
    int MaximumCorrectionAttempts,
    TimeSpan MaximumElapsedTime,
    double TargetInsideFieldMarginPixels)
{
    public const int CurrentSchemaVersion = 1;

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
        {
            issues.Add($"G3 WCS-centering schema must be {CurrentSchemaVersion}.");
        }
        if (!Positive(MaximumSingleCorrectionArcseconds))
        {
            issues.Add("G3 WCS-centering single correction must be positive and finite.");
        }
        if (!Positive(MaximumRadiusArcseconds))
        {
            issues.Add("G3 WCS-centering radius must be positive and finite.");
        }
        if (Positive(MaximumSingleCorrectionArcseconds) &&
            Positive(MaximumRadiusArcseconds) &&
            MaximumSingleCorrectionArcseconds > MaximumRadiusArcseconds)
        {
            issues.Add("G3 WCS-centering single correction cannot exceed its radius.");
        }
        if (!Positive(MaximumCumulativeMotionArcseconds) ||
            (Positive(MaximumSingleCorrectionArcseconds) &&
             MaximumCumulativeMotionArcseconds < 2 * MaximumSingleCorrectionArcseconds))
        {
            issues.Add("G3 WCS-centering cumulative motion must reserve at least one outbound correction and its return.");
        }
        if (MaximumCorrectionAttempts < 2)
        {
            issues.Add("G3 WCS-centering must reserve at least one outbound and one return action.");
        }
        if (MaximumElapsedTime <= TimeSpan.Zero)
        {
            issues.Add("G3 WCS-centering elapsed-time limit must be positive.");
        }
        if (!double.IsFinite(TargetInsideFieldMarginPixels) || TargetInsideFieldMarginPixels < 0)
        {
            issues.Add("G3 target-inside-field margin must be finite and non-negative.");
        }
        return issues.AsReadOnly();
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
}

public static class G3SolvedFieldPolicy
{
    public static GateResult TargetInsideField(
        double targetX,
        double targetY,
        int imageWidth,
        int imageHeight,
        double marginPixels)
    {
        if (!double.IsFinite(targetX) || !double.IsFinite(targetY) ||
            imageWidth <= 0 || imageHeight <= 0 ||
            !double.IsFinite(marginPixels) || marginPixels < 0 ||
            marginPixels * 2 >= imageWidth || marginPixels * 2 >= imageHeight)
        {
            return GateResult.Unknown(
                "G3_SOLVED_FIELD_GEOMETRY_INVALID",
                "The solved target projection, image dimensions or field margin is invalid.");
        }
        if (targetX < marginPixels || targetX >= imageWidth - marginPixels ||
            targetY < marginPixels || targetY >= imageHeight - marginPixels)
        {
            return GateResult.Unknown(
                "G3_SOLVED_TARGET_OUTSIDE",
                $"The fresh G3 WCS projects the requested target to ({targetX:F1},{targetY:F1}), outside the {imageWidth}x{imageHeight} field with {marginPixels:F1}px margin.",
                new Dictionary<string, double>
                {
                    ["targetX"] = targetX,
                    ["targetY"] = targetY,
                    ["imageWidth"] = imageWidth,
                    ["imageHeight"] = imageHeight,
                    ["fieldMarginPixels"] = marginPixels,
                });
        }
        return GateResult.Pass(
            "G3_SOLVED_TARGET_INSIDE",
            "The fresh G3 WCS places the requested target inside the usable detector field.",
            new Dictionary<string, double>
            {
                ["targetX"] = targetX,
                ["targetY"] = targetY,
            });
    }
}

public enum G3AcquisitionMotionKind
{
    LocalSearch,
    WcsCentering,
}

public enum G3AcquisitionMotionPhase
{
    OutboundIntent,
    AwaitingFreshSolve,
    ReturnIntent,
    SettledBudgetLedger,
}

/// <summary>
/// Durable authority and conservative budget for G3 acquisition motion. The
/// origin is a reported mount coordinate, never a compiled optical offset.
/// Every accepted outbound or return command is precharged before its intent
/// is written atomically.
/// </summary>
public sealed record G3AcquisitionMotionState(
    int SchemaVersion,
    string TangentProjectionId,
    string ObservationRunId,
    string BudgetLineageId,
    string ActionConfigurationSha256,
    string RecoveryContextSha256,
    string CommissioningPresetSha256,
    G3AcquisitionMotionKind Kind,
    G3AcquisitionMotionPhase Phase,
    string PierSide,
    string CoordinateEpoch,
    double OriginRaDegrees,
    double OriginDeclinationDegrees,
    double PriorReportedRaDegrees,
    double PriorReportedDeclinationDegrees,
    double CommandedRaDegrees,
    double CommandedDeclinationDegrees,
    double CurrentRaTangentOffsetArcseconds,
    double CurrentDeclinationOffsetArcseconds,
    double CommandMagnitudeArcseconds,
    double MaximumSingleCorrectionArcseconds,
    double MaximumRadiusArcseconds,
    double MaximumCumulativeMotionArcseconds,
    int MaximumCorrectionAttempts,
    double ArrivalToleranceArcseconds,
    double WorstCaseActionSeconds,
    double MaximumElapsedSeconds,
    double CumulativeMotionArcseconds,
    int CorrectionAttempts,
    DateTimeOffset StartedUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string DeclaredEvidencePath,
    string? LastReason = null)
{
    // Schema 2 changes persisted offsets to the versioned TAN projection.
    // Older outstanding ledgers fail closed instead of being geometrically
    // reinterpreted by a later executable.
    public const int CurrentSchemaVersion = 2;
    public const string CurrentTangentProjectionId = "ICRS-GNOMONIC-TAN-V1";

    public double CurrentRadiusArcseconds
    {
        get
        {
            var current = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(
                OriginRaDegrees,
                OriginDeclinationDegrees,
                CurrentRaTangentOffsetArcseconds,
                CurrentDeclinationOffsetArcseconds);
            return G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
                OriginRaDegrees,
                OriginDeclinationDegrees,
                current.RaDegrees,
                current.DecDegrees);
        }
    }

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion) issues.Add($"G3 acquisition-motion schema must be {CurrentSchemaVersion}.");
        if (!string.Equals(TangentProjectionId, CurrentTangentProjectionId, StringComparison.Ordinal))
        {
            issues.Add($"G3 tangent projection must be '{CurrentTangentProjectionId}'.");
        }
        if (string.IsNullOrWhiteSpace(ObservationRunId)) issues.Add("Observation run id is missing.");
        if (!Guid.TryParseExact(BudgetLineageId, "N", out _)) issues.Add("Budget lineage id must be a 32-character GUID in N format.");
        if (!IsSha256(ActionConfigurationSha256)) issues.Add("Action-configuration SHA-256 is invalid.");
        if (!IsSha256(RecoveryContextSha256)) issues.Add("Recovery-context SHA-256 is invalid.");
        if (!IsSha256(CommissioningPresetSha256)) issues.Add("Commissioning-preset SHA-256 is invalid.");
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Phase)) issues.Add("G3 acquisition-motion kind or phase is undefined.");
        if (string.IsNullOrWhiteSpace(PierSide) || PierSide.Contains("unknown", StringComparison.OrdinalIgnoreCase)) issues.Add("A known exact pier side is required.");
        if (string.IsNullOrWhiteSpace(CoordinateEpoch)) issues.Add("The reported mount coordinate epoch is missing.");
        ValidateRa(issues, OriginRaDegrees, "origin RA");
        ValidateDec(issues, OriginDeclinationDegrees, "origin Dec");
        ValidateRa(issues, PriorReportedRaDegrees, "prior reported RA");
        ValidateDec(issues, PriorReportedDeclinationDegrees, "prior reported Dec");
        ValidateRa(issues, CommandedRaDegrees, "commanded RA");
        ValidateDec(issues, CommandedDeclinationDegrees, "commanded Dec");
        if (!double.IsFinite(CurrentRaTangentOffsetArcseconds) || !double.IsFinite(CurrentDeclinationOffsetArcseconds)) issues.Add("Current tangent-plane offset is invalid.");
        if (!Positive(MaximumSingleCorrectionArcseconds) || !Positive(MaximumRadiusArcseconds) ||
            MaximumSingleCorrectionArcseconds > MaximumRadiusArcseconds)
        {
            issues.Add("Single-motion/radius limits are invalid.");
        }
        if (!Positive(MaximumCumulativeMotionArcseconds) || MaximumCumulativeMotionArcseconds < 2 * MaximumSingleCorrectionArcseconds)
        {
            issues.Add("Cumulative-motion limit does not reserve an outbound and return.");
        }
        if (MaximumCorrectionAttempts < 2 || !Positive(ArrivalToleranceArcseconds) ||
            2 * ArrivalToleranceArcseconds >= MaximumSingleCorrectionArcseconds ||
            !Positive(WorstCaseActionSeconds) || !Positive(MaximumElapsedSeconds))
        {
            issues.Add("Attempt/arrival/action-duration/elapsed-time limits are invalid.");
        }
        if (!double.IsFinite(CumulativeMotionArcseconds) || CumulativeMotionArcseconds < 0 ||
            CumulativeMotionArcseconds > MaximumCumulativeMotionArcseconds + 1e-9)
        {
            issues.Add("Consumed cumulative motion is outside the declared limit.");
        }
        if (CorrectionAttempts < 0 || CorrectionAttempts > MaximumCorrectionAttempts) issues.Add("Consumed correction attempts are outside the declared limit.");
        if (Phase != G3AcquisitionMotionPhase.SettledBudgetLedger &&
            (!Positive(CommandMagnitudeArcseconds) || CommandMagnitudeArcseconds > MaximumSingleCorrectionArcseconds + 1e-9))
        {
            issues.Add("An outstanding motion intent must contain a positive, bounded command magnitude.");
        }
        if (Phase != G3AcquisitionMotionPhase.SettledBudgetLedger &&
            (CorrectionAttempts < 1 || CumulativeMotionArcseconds + 1e-9 < CommandMagnitudeArcseconds))
        {
            issues.Add("An outstanding motion intent must conservatively precharge its command.");
        }
        if (StartedUtc == default || CreatedUtc < StartedUtc || UpdatedUtc < CreatedUtc) issues.Add("G3 acquisition-motion timestamps are invalid.");
        if (string.IsNullOrWhiteSpace(DeclaredEvidencePath)) issues.Add("Declared recovery evidence path is missing.");
        return issues.AsReadOnly();
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool IsSha256(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static void ValidateRa(ICollection<string> issues, double value, string label)
    {
        if (!double.IsFinite(value) || value is < 0 or >= 360) issues.Add($"{label} must be finite in [0,360).");
    }

    private static void ValidateDec(ICollection<string> issues, double value, string label)
    {
        if (!double.IsFinite(value) || value is < -90 or > 90) issues.Add($"{label} must be finite in [-90,90].");
    }
}

public sealed record G3AcquisitionMotionReserve(
    GateResult Gate,
    double MoveFromCurrentArcseconds,
    double NextRadiusArcseconds,
    int ReservedReturnMoves);

public sealed record G3SphericalCommandValidation(
    GateResult Gate,
    double CommandDistanceArcseconds,
    double EndpointRadiusArcseconds);

public sealed record G3AcquisitionReturnStep(
    GateResult Gate,
    bool AlreadyAtOrigin,
    double CurrentRadiusArcseconds,
    double CommandedRaDegrees,
    double CommandedDeclinationDegrees,
    double CommandMagnitudeArcseconds);

public static class G3AcquisitionMotionPlanner
{
    public static G3AcquisitionMotionState ContinueSettledLedger(
        G3AcquisitionMotionState state,
        string observationRunId,
        G3AcquisitionMotionKind kind,
        string declaredEvidencePath,
        DateTimeOffset nowUtc,
        double? familyMaximumSingleCorrectionArcseconds = null,
        double? familyMaximumRadiusArcseconds = null,
        double? familyAdditionalCumulativeMotionArcseconds = null,
        int? familyAdditionalCorrectionAttempts = null,
        TimeSpan? familyAdditionalElapsedTime = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var issues = state.Validate();
        if (issues.Count > 0) throw new InvalidOperationException(string.Join(" ", issues));
        if (state.Phase != G3AcquisitionMotionPhase.SettledBudgetLedger)
        {
            throw new InvalidOperationException("Only a settled durable G3 ledger can begin another motion family.");
        }
        if (string.IsNullOrWhiteSpace(observationRunId)) throw new ArgumentException("Observation run id is required.", nameof(observationRunId));
        if (string.IsNullOrWhiteSpace(declaredEvidencePath)) throw new ArgumentException("Declared evidence path is required.", nameof(declaredEvidencePath));
        if (nowUtc < state.UpdatedUtc) throw new ArgumentOutOfRangeException(nameof(nowUtc), "Continuation time cannot precede the durable ledger update.");

        var maximumSingle = familyMaximumSingleCorrectionArcseconds is { } requestedSingle
            ? Math.Min(state.MaximumSingleCorrectionArcseconds, requestedSingle)
            : state.MaximumSingleCorrectionArcseconds;
        var maximumRadius = familyMaximumRadiusArcseconds is { } requestedRadius
            ? Math.Min(state.MaximumRadiusArcseconds, requestedRadius)
            : state.MaximumRadiusArcseconds;
        var maximumCumulative = state.MaximumCumulativeMotionArcseconds;
        if (familyAdditionalCumulativeMotionArcseconds is { } additionalCumulative)
        {
            if (!double.IsFinite(additionalCumulative) || additionalCumulative <= 0)
                throw new InvalidOperationException("The current-family cumulative-motion increment must be finite and positive.");
            maximumCumulative = Math.Min(
                maximumCumulative,
                checked(state.CumulativeMotionArcseconds + additionalCumulative));
        }
        var maximumAttempts = state.MaximumCorrectionAttempts;
        if (familyAdditionalCorrectionAttempts is { } additionalAttempts)
        {
            if (additionalAttempts <= 0)
                throw new InvalidOperationException("The current-family correction-attempt increment must be positive.");
            maximumAttempts = Math.Min(
                maximumAttempts,
                checked(state.CorrectionAttempts + additionalAttempts));
        }
        var maximumElapsedSeconds = state.MaximumElapsedSeconds;
        if (familyAdditionalElapsedTime is { } additionalElapsed)
        {
            if (additionalElapsed <= TimeSpan.Zero || !double.IsFinite(additionalElapsed.TotalSeconds))
                throw new InvalidOperationException("The current-family elapsed-time increment must be finite and positive.");
            var consumedElapsedSeconds = Math.Max(0, (nowUtc - state.StartedUtc).TotalSeconds);
            maximumElapsedSeconds = Math.Min(
                maximumElapsedSeconds,
                checked(consumedElapsedSeconds + additionalElapsed.TotalSeconds));
        }
        if (!double.IsFinite(maximumSingle) || !double.IsFinite(maximumRadius) ||
            maximumSingle <= 0 || maximumRadius <= 0 || maximumSingle > maximumRadius ||
            2 * state.ArrivalToleranceArcseconds >= maximumSingle ||
            !double.IsFinite(maximumCumulative) || maximumCumulative < state.CumulativeMotionArcseconds ||
            maximumAttempts < state.CorrectionAttempts ||
            !double.IsFinite(maximumElapsedSeconds) ||
            maximumElapsedSeconds < Math.Max(0, (nowUtc - state.StartedUtc).TotalSeconds))
        {
            throw new InvalidOperationException("The continued G3 motion-family limits are invalid, already consumed, or incompatible with the inherited arrival tolerance.");
        }

        // Deliberately preserve lineage, origin, limits, consumed motion,
        // attempts and the earliest start. A process/run handoff can never mint
        // fresh budget merely because the prior intent is settled.
        return state with
        {
            ObservationRunId = observationRunId,
            Kind = kind,
            Phase = G3AcquisitionMotionPhase.SettledBudgetLedger,
            CommandMagnitudeArcseconds = 0,
            MaximumSingleCorrectionArcseconds = maximumSingle,
            MaximumRadiusArcseconds = maximumRadius,
            MaximumCumulativeMotionArcseconds = maximumCumulative,
            MaximumCorrectionAttempts = maximumAttempts,
            MaximumElapsedSeconds = maximumElapsedSeconds,
            DeclaredEvidencePath = declaredEvidencePath,
            UpdatedUtc = nowUtc,
            LastReason = $"Settled G3 budget lineage continued for {kind} with current-family limits no wider than the prior family and without resetting motion, action or elapsed-time consumption.",
        };
    }

    public static G3AcquisitionMotionReserve ValidateOutboundAndReturnReserve(
        G3AcquisitionMotionState state,
        double nextRaTangentOffsetArcseconds,
        double nextDeclinationOffsetArcseconds,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        var issues = state.Validate();
        if (issues.Count > 0) return BlockReserve("G3_MOTION_LEDGER_INVALID", string.Join(" ", issues));
        if (!double.IsFinite(nextRaTangentOffsetArcseconds) || !double.IsFinite(nextDeclinationOffsetArcseconds))
        {
            return BlockReserve("G3_MOTION_NEXT_OFFSET_INVALID", "The next tangent-plane offset is not finite.");
        }
        if (nowUtc - state.StartedUtc > TimeSpan.FromSeconds(state.MaximumElapsedSeconds))
        {
            return BlockReserve("G3_MOTION_TIME_LIMIT", "The G3 acquisition-motion elapsed-time limit has been reached.");
        }
        var current = ApplyTangentOffsetArcseconds(
            state.OriginRaDegrees,
            state.OriginDeclinationDegrees,
            state.CurrentRaTangentOffsetArcseconds,
            state.CurrentDeclinationOffsetArcseconds);
        var next = ApplyTangentOffsetArcseconds(
            state.OriginRaDegrees,
            state.OriginDeclinationDegrees,
            nextRaTangentOffsetArcseconds,
            nextDeclinationOffsetArcseconds);
        var move = AngularSeparationArcseconds(
            current.RaDegrees,
            current.DecDegrees,
            next.RaDegrees,
            next.DecDegrees);
        var radius = AngularSeparationArcseconds(
            state.OriginRaDegrees,
            state.OriginDeclinationDegrees,
            next.RaDegrees,
            next.DecDegrees);
        var maximumCommandArcseconds = state.MaximumSingleCorrectionArcseconds - state.ArrivalToleranceArcseconds;
        if (!double.IsFinite(move) || move <= 0 || move > maximumCommandArcseconds + 1e-9)
        {
            return BlockReserve("G3_MOTION_SINGLE_LIMIT", "The proposed outbound command plus its allowed arrival error exceeds the single-motion limit.", move, radius);
        }
        if (radius > state.MaximumRadiusArcseconds + 1e-9)
        {
            return BlockReserve("G3_MOTION_RADIUS_LIMIT", "The proposed outbound endpoint exceeds the declared radius.", move, radius);
        }
        // A physical endpoint may lie one full arrival tolerance in the
        // radially wrong direction. The guaranteed progress of a maximum
        // return command is therefore command-tolerance, not command.
        var guaranteedProgress = maximumCommandArcseconds - state.ArrivalToleranceArcseconds;
        var returnMoves = radius <= 0
            ? 0
            : checked((int)Math.Ceiling(radius / guaranteedProgress));
        var requiredCumulative = state.CumulativeMotionArcseconds +
            move + state.ArrivalToleranceArcseconds +
            returnMoves * (maximumCommandArcseconds + state.ArrivalToleranceArcseconds);
        if (requiredCumulative > state.MaximumCumulativeMotionArcseconds + 1e-9)
        {
            return BlockReserve("G3_MOTION_RETURN_CUMULATIVE_RESERVE_LIMIT", "The outbound move plus a return to the reported origin exceeds the cumulative-motion envelope.", move, radius);
        }
        if (state.CorrectionAttempts + 1 + returnMoves > state.MaximumCorrectionAttempts)
        {
            return BlockReserve("G3_MOTION_RETURN_ATTEMPT_RESERVE_LIMIT", "The outbound move plus its segmented return exceeds the action-count envelope.", move, radius, returnMoves);
        }
        var requiredSeconds = (1 + returnMoves) * state.WorstCaseActionSeconds;
        if ((nowUtc - state.StartedUtc).TotalSeconds + requiredSeconds > state.MaximumElapsedSeconds + 1e-9)
        {
            return BlockReserve(
                "G3_MOTION_RETURN_TIME_RESERVE_LIMIT",
                "The outbound action plus every worst-case return action does not fit the durable elapsed-time envelope.",
                move,
                radius,
                returnMoves);
        }
        return new G3AcquisitionMotionReserve(
            GateResult.Pass("G3_MOTION_OUTBOUND_AND_RETURN_RESERVED", "The outbound move and a segmented return to the reported origin are reserved."),
            move,
            radius,
            returnMoves);
    }

    public static G3AcquisitionReturnStep PlanNextReturnStep(
        G3AcquisitionMotionState state,
        double reportedRaDegrees,
        double reportedDeclinationDegrees,
        double arrivalToleranceArcseconds,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        var issues = state.Validate();
        if (issues.Count > 0) return BlockReturn("G3_MOTION_LEDGER_INVALID", string.Join(" ", issues));
        if (!double.IsFinite(arrivalToleranceArcseconds) || arrivalToleranceArcseconds <= 0)
        {
            return BlockReturn("G3_MOTION_RETURN_TOLERANCE_INVALID", "The return arrival tolerance is invalid.");
        }
        var radius = AngularSeparationArcseconds(
            state.OriginRaDegrees,
            state.OriginDeclinationDegrees,
            reportedRaDegrees,
            reportedDeclinationDegrees);
        if (!double.IsFinite(radius)) return BlockReturn("G3_MOTION_REPORTED_POSITION_INVALID", "The reported origin offset is invalid.");
        if (radius <= arrivalToleranceArcseconds)
        {
            return new G3AcquisitionReturnStep(
                GateResult.Pass("G3_MOTION_ORIGIN_REACHED", "The mount's reported position is at the durable G3 acquisition origin."),
                true,
                radius,
                state.OriginRaDegrees,
                state.OriginDeclinationDegrees,
                0);
        }
        if (radius > state.MaximumRadiusArcseconds + arrivalToleranceArcseconds)
        {
            return BlockReturn("G3_MOTION_RETURN_OUTSIDE_RADIUS", "The reported mount position is outside the durable G3 acquisition radius; automatic return is prohibited.", radius);
        }
        var maximumCommandArcseconds = state.MaximumSingleCorrectionArcseconds - state.ArrivalToleranceArcseconds;
        var move = Math.Min(radius, maximumCommandArcseconds);
        if (state.CumulativeMotionArcseconds + move + state.ArrivalToleranceArcseconds > state.MaximumCumulativeMotionArcseconds + 1e-9)
        {
            return BlockReturn("G3_MOTION_RETURN_CUMULATIVE_LIMIT", "The next return step no longer fits the durable cumulative-motion budget.", radius);
        }
        if (state.CorrectionAttempts >= state.MaximumCorrectionAttempts)
        {
            return BlockReturn("G3_MOTION_RETURN_ATTEMPT_LIMIT", "The next return step no longer fits the durable action-count budget.", radius);
        }
        if ((nowUtc - state.StartedUtc).TotalSeconds + state.WorstCaseActionSeconds > state.MaximumElapsedSeconds + 1e-9)
        {
            return BlockReturn("G3_MOTION_RETURN_TIME_LIMIT", "The next worst-case return action no longer fits the durable elapsed-time envelope.", radius);
        }
        var fractionRemaining = Math.Max(0, 1 - move / radius);
        var (ra, dec) = InterpolateGreatCircle(
            state.OriginRaDegrees,
            state.OriginDeclinationDegrees,
            reportedRaDegrees,
            reportedDeclinationDegrees,
            fractionRemaining);
        if (!double.IsFinite(ra) || !double.IsFinite(dec))
        {
            return BlockReturn("G3_MOTION_RETURN_COORDINATE_INVALID", "The next segmented return coordinate is invalid.", radius);
        }
        var actualMove = AngularSeparationArcseconds(reportedRaDegrees, reportedDeclinationDegrees, ra, dec);
        if (!double.IsFinite(actualMove) || actualMove > maximumCommandArcseconds + 1e-6)
        {
            return BlockReturn("G3_MOTION_RETURN_SPHERICAL_LIMIT", "The spherical distance of the next return coordinate exceeds the reserved single-command limit.", radius);
        }
        return new G3AcquisitionReturnStep(
            GateResult.Pass("G3_MOTION_RETURN_STEP_BOUNDED", $"The next durable G3 return step is {actualMove:F2} arcsec."),
            false,
            radius,
            ra,
            dec,
            actualMove);
    }

    public static (double RaArcseconds, double DecArcseconds) SignedTangentOffsetArcseconds(
        double originRaDegrees,
        double originDeclinationDegrees,
        double targetRaDegrees,
        double targetDeclinationDegrees)
    {
        if (!double.IsFinite(originRaDegrees) || originRaDegrees is < 0 or >= 360 ||
            !double.IsFinite(originDeclinationDegrees) || originDeclinationDegrees is < -90 or > 90 ||
            !double.IsFinite(targetRaDegrees) || targetRaDegrees is < 0 or >= 360 ||
            !double.IsFinite(targetDeclinationDegrees) || targetDeclinationDegrees is < -90 or > 90)
        {
            return (double.NaN, double.NaN);
        }
        var ra0 = DegreesToRadians(originRaDegrees);
        var dec0 = DegreesToRadians(originDeclinationDegrees);
        var ra = DegreesToRadians(targetRaDegrees);
        var dec = DegreesToRadians(targetDeclinationDegrees);
        var deltaRa = WrapRadians(ra - ra0);
        var sinDec0 = Math.Sin(dec0);
        var cosDec0 = Math.Cos(dec0);
        var sinDec = Math.Sin(dec);
        var cosDec = Math.Cos(dec);
        if (Math.Abs(cosDec0) <= PoleCoordinateTolerance || Math.Abs(cosDec) <= PoleCoordinateTolerance)
        {
            // RA is not an observable coordinate at an exact celestial pole,
            // so an RA-bearing durable command must fail closed there.
            return (double.NaN, double.NaN);
        }
        var denominator = sinDec0 * sinDec + cosDec0 * cosDec * Math.Cos(deltaRa);
        if (!double.IsFinite(denominator) || denominator <= ProjectionDenominatorTolerance) return (double.NaN, double.NaN);
        var xi = cosDec * Math.Sin(deltaRa) / denominator;
        var eta = (cosDec0 * sinDec - sinDec0 * cosDec * Math.Cos(deltaRa)) / denominator;
        return (xi * ArcsecondsPerRadian, eta * ArcsecondsPerRadian);
    }

    public static (double RaDegrees, double DecDegrees) ApplyTangentOffsetArcseconds(
        double originRaDegrees,
        double originDeclinationDegrees,
        double raArcseconds,
        double decArcseconds)
    {
        if (!double.IsFinite(originRaDegrees) || originRaDegrees is < 0 or >= 360 ||
            !double.IsFinite(originDeclinationDegrees) || originDeclinationDegrees is < -90 or > 90 ||
            !double.IsFinite(raArcseconds) || !double.IsFinite(decArcseconds))
        {
            return (double.NaN, double.NaN);
        }
        var x = raArcseconds / ArcsecondsPerRadian;
        var y = decArcseconds / ArcsecondsPerRadian;
        var rho = Math.Sqrt(x * x + y * y);
        if (!double.IsFinite(rho)) return (double.NaN, double.NaN);
        if (rho <= 1e-15) return (NormalizeRaDegrees(originRaDegrees), originDeclinationDegrees);

        var ra0 = DegreesToRadians(originRaDegrees);
        var dec0 = DegreesToRadians(originDeclinationDegrees);
        var c = Math.Atan(rho);
        var sinC = Math.Sin(c);
        var cosC = Math.Cos(c);
        var sinDec0 = Math.Sin(dec0);
        var cosDec0 = Math.Cos(dec0);
        var dec = Math.Asin(Clamp(cosC * sinDec0 + y * sinC * cosDec0 / rho, -1, 1));
        var ra = ra0 + Math.Atan2(
            x * sinC,
            rho * cosDec0 * cosC - y * sinDec0 * sinC);
        if (Math.Abs(Math.Cos(dec)) <= PoleCoordinateTolerance) return (double.NaN, double.NaN);
        return (NormalizeRaDegrees(RadiansToDegrees(ra)), RadiansToDegrees(dec));
    }

    public static double AngularSeparationArcseconds(
        double firstRaDegrees,
        double firstDeclinationDegrees,
        double secondRaDegrees,
        double secondDeclinationDegrees)
    {
        if (!ValidCoordinate(firstRaDegrees, firstDeclinationDegrees) ||
            !ValidCoordinate(secondRaDegrees, secondDeclinationDegrees))
        {
            return double.NaN;
        }
        var first = UnitVector(firstRaDegrees, firstDeclinationDegrees);
        var second = UnitVector(secondRaDegrees, secondDeclinationDegrees);
        var crossX = first.Y * second.Z - first.Z * second.Y;
        var crossY = first.Z * second.X - first.X * second.Z;
        var crossZ = first.X * second.Y - first.Y * second.X;
        var crossNorm = Math.Sqrt(crossX * crossX + crossY * crossY + crossZ * crossZ);
        var dot = Clamp(first.X * second.X + first.Y * second.Y + first.Z * second.Z, -1, 1);
        return Math.Atan2(crossNorm, dot) * ArcsecondsPerRadian;
    }

    public static G3SphericalCommandValidation ValidateSphericalCommand(
        G3AcquisitionMotionState state,
        double reportedRaDegrees,
        double reportedDeclinationDegrees,
        double commandedRaDegrees,
        double commandedDeclinationDegrees,
        double reservedCommandArcseconds)
    {
        ArgumentNullException.ThrowIfNull(state);
        var issues = state.Validate();
        if (issues.Count > 0)
        {
            return BlockSphericalCommand("G3_MOTION_LEDGER_INVALID", string.Join(" ", issues));
        }
        if (!double.IsFinite(reservedCommandArcseconds) || reservedCommandArcseconds <= 0)
        {
            return BlockSphericalCommand("G3_MOTION_RESERVED_COMMAND_INVALID", "The reserved G3 command magnitude is not finite and positive.");
        }
        var commandDistance = AngularSeparationArcseconds(
            reportedRaDegrees,
            reportedDeclinationDegrees,
            commandedRaDegrees,
            commandedDeclinationDegrees);
        var endpointRadius = AngularSeparationArcseconds(
            state.OriginRaDegrees,
            state.OriginDeclinationDegrees,
            commandedRaDegrees,
            commandedDeclinationDegrees);
        if (!double.IsFinite(commandDistance) || !double.IsFinite(endpointRadius))
        {
            return BlockSphericalCommand("G3_MOTION_SPHERICAL_DISTANCE_INVALID", "The reported-to-commanded spherical distance is invalid.");
        }
        var allowedDistance = Math.Min(
            state.MaximumSingleCorrectionArcseconds,
            reservedCommandArcseconds + state.ArrivalToleranceArcseconds);
        if (commandDistance > allowedDistance + 1e-6)
        {
            return BlockSphericalCommand(
                "G3_MOTION_SPHERICAL_SINGLE_LIMIT",
                "The fresh reported-to-commanded spherical distance exceeds the reserved-plus-arrival or single-command limit.",
                commandDistance,
                endpointRadius);
        }
        if (endpointRadius > state.MaximumRadiusArcseconds + state.ArrivalToleranceArcseconds + 1e-6)
        {
            return BlockSphericalCommand(
                "G3_MOTION_SPHERICAL_RADIUS_LIMIT",
                "The commanded coordinate is outside the durable origin radius after allowing one arrival tolerance.",
                commandDistance,
                endpointRadius);
        }
        return new G3SphericalCommandValidation(
            GateResult.Pass(
                "G3_MOTION_SPHERICAL_COMMAND_BOUNDED",
                "The fresh reported-to-commanded spherical distance is finite and within the reserved command, arrival and radius envelopes.",
                new Dictionary<string, double>
                {
                    ["commandDistanceArcseconds"] = commandDistance,
                    ["endpointRadiusArcseconds"] = endpointRadius,
                    ["reservedCommandArcseconds"] = reservedCommandArcseconds,
                }),
            commandDistance,
            endpointRadius);
    }

    private static (double RaDegrees, double DecDegrees) InterpolateGreatCircle(
        double originRaDegrees,
        double originDeclinationDegrees,
        double targetRaDegrees,
        double targetDeclinationDegrees,
        double targetFraction)
    {
        if (!ValidCoordinate(originRaDegrees, originDeclinationDegrees) ||
            !ValidCoordinate(targetRaDegrees, targetDeclinationDegrees) ||
            !double.IsFinite(targetFraction) || targetFraction is < 0 or > 1)
        {
            return (double.NaN, double.NaN);
        }
        var origin = UnitVector(originRaDegrees, originDeclinationDegrees);
        var target = UnitVector(targetRaDegrees, targetDeclinationDegrees);
        var dot = Clamp(origin.X * target.X + origin.Y * target.Y + origin.Z * target.Z, -1, 1);
        var angle = Math.Acos(dot);
        if (angle <= 1e-15) return (NormalizeRaDegrees(originRaDegrees), originDeclinationDegrees);
        if (Math.PI - angle <= 1e-12) return (double.NaN, double.NaN);
        var sinAngle = Math.Sin(angle);
        var originWeight = Math.Sin((1 - targetFraction) * angle) / sinAngle;
        var targetWeight = Math.Sin(targetFraction * angle) / sinAngle;
        var x = originWeight * origin.X + targetWeight * target.X;
        var y = originWeight * origin.Y + targetWeight * target.Y;
        var z = originWeight * origin.Z + targetWeight * target.Z;
        var norm = Math.Sqrt(x * x + y * y + z * z);
        if (!double.IsFinite(norm) || norm <= 0) return (double.NaN, double.NaN);
        x /= norm;
        y /= norm;
        z /= norm;
        var ra = NormalizeRaDegrees(RadiansToDegrees(Math.Atan2(y, x)));
        var dec = RadiansToDegrees(Math.Asin(Clamp(z, -1, 1)));
        return (ra, dec);
    }

    private static bool ValidCoordinate(double raDegrees, double decDegrees) =>
        double.IsFinite(raDegrees) && raDegrees is >= 0 and < 360 &&
        double.IsFinite(decDegrees) && decDegrees is >= -90 and <= 90;

    private static (double X, double Y, double Z) UnitVector(double raDegrees, double decDegrees)
    {
        var ra = DegreesToRadians(raDegrees);
        var dec = DegreesToRadians(decDegrees);
        var cosDec = Math.Cos(dec);
        return (cosDec * Math.Cos(ra), cosDec * Math.Sin(ra), Math.Sin(dec));
    }

    private static double WrapRadians(double radians)
    {
        var wrapped = radians % (2 * Math.PI);
        if (wrapped > Math.PI) wrapped -= 2 * Math.PI;
        if (wrapped < -Math.PI) wrapped += 2 * Math.PI;
        return wrapped;
    }

    private static double NormalizeRaDegrees(double degrees) => ((degrees % 360) + 360) % 360;
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
    private static double RadiansToDegrees(double radians) => radians * 180 / Math.PI;
    private static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));
    private const double ArcsecondsPerRadian = 206264.80624709636;
    private const double ProjectionDenominatorTolerance = 1.4210854715202004e-14; // 64 * double epsilon
    private const double PoleCoordinateTolerance = 3.552713678800501e-15; // 16 * double epsilon

    private static G3AcquisitionMotionReserve BlockReserve(
        string code,
        string message,
        double move = double.NaN,
        double radius = double.NaN,
        int returnMoves = 0) =>
        new(GateResult.Unknown(code, message), move, radius, returnMoves);

    private static G3AcquisitionReturnStep BlockReturn(
        string code,
        string message,
        double radius = double.NaN) =>
        new(GateResult.Unknown(code, message), false, radius, double.NaN, double.NaN, double.NaN);

    private static G3SphericalCommandValidation BlockSphericalCommand(
        string code,
        string message,
        double commandDistance = double.NaN,
        double endpointRadius = double.NaN) =>
        new(GateResult.Unknown(code, message), commandDistance, endpointRadius);
}

public sealed record G3AcquisitionMotionLoadResult(G3AcquisitionMotionState? State, string? Error);
public sealed record G3AcquisitionMotionFileResult(string Path, G3AcquisitionMotionState? State, string? Error);

public static class G3AcquisitionMotionStore
{
    private const int EnvelopeSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task WriteAtomicAsync(
        string path,
        G3AcquisitionMotionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);
        var issues = state.Validate();
        if (issues.Count > 0) throw new InvalidOperationException(string.Join(" ", issues));
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            var envelope = new G3AcquisitionMotionEnvelope(
                EnvelopeSchemaVersion,
                Convert.ToHexString(SHA256.HashData(stateBytes)),
                state);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1 << 14,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static async Task<G3AcquisitionMotionLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return new G3AcquisitionMotionLoadResult(null, null);
        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1 << 14,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var envelope = await JsonSerializer.DeserializeAsync<G3AcquisitionMotionEnvelope>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (envelope is null) return new G3AcquisitionMotionLoadResult(null, "The G3 acquisition-motion envelope contains JSON null.");
            if (envelope.EnvelopeSchemaVersion != EnvelopeSchemaVersion)
            {
                return new G3AcquisitionMotionLoadResult(null, $"G3 acquisition-motion envelope schema must be {EnvelopeSchemaVersion}.");
            }
            if (envelope.State is null || !IsSha256(envelope.StateSha256))
            {
                return new G3AcquisitionMotionLoadResult(null, "The G3 acquisition-motion state or SHA-256 is missing.");
            }
            var canonical = JsonSerializer.SerializeToUtf8Bytes(envelope.State, JsonOptions);
            var actual = SHA256.HashData(canonical);
            var expected = Convert.FromHexString(envelope.StateSha256);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                return new G3AcquisitionMotionLoadResult(null, "The G3 acquisition-motion canonical payload SHA-256 does not match.");
            }
            var issues = envelope.State.Validate();
            return issues.Count == 0
                ? new G3AcquisitionMotionLoadResult(envelope.State, null)
                : new G3AcquisitionMotionLoadResult(null, string.Join(" ", issues));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return new G3AcquisitionMotionLoadResult(null, ex.Message);
        }
    }

    public static async Task<IReadOnlyList<G3AcquisitionMotionFileResult>> DiscoverAsync(
        string observationsRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationsRoot);
        var root = Path.GetFullPath(observationsRoot);
        if (!Directory.Exists(root)) return Array.Empty<G3AcquisitionMotionFileResult>();
        string[] runDirectories;
        try
        {
            runDirectories = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [new G3AcquisitionMotionFileResult(root, null, ex.Message)];
        }
        var results = new List<G3AcquisitionMotionFileResult>();
        foreach (var runDirectory in runDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(runDirectory, "control", "g3-acquisition-motion.json");
            if (!File.Exists(path)) continue;
            var loaded = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
            results.Add(new G3AcquisitionMotionFileResult(Path.GetFullPath(path), loaded.State, loaded.Error));
        }
        return results.AsReadOnly();
    }

    private static bool IsSha256(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed record G3AcquisitionMotionEnvelope(
        int EnvelopeSchemaVersion,
        string StateSha256,
        G3AcquisitionMotionState State);
}
