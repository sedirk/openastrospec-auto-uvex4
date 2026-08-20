using System.Text.Json;
using System.Security.Cryptography;

namespace UvexAdv.Observatory;

public enum SlitPlacementPendingPhase
{
    MoveIntent,
    AwaitingFreshField,
    ReturnRequired,
    SettledBudgetLedger,
}

/// <summary>
/// Durable, run-bound recovery authority for exactly one slit-placement
/// segment. The origin is the reported position immediately before that
/// segment, not a two-telescope optical offset.
/// </summary>
public sealed record SlitPlacementPendingState(
    int SchemaVersion,
    string ObservationRunId,
    string BudgetLineageId,
    string ActionConfigurationSha256,
    string RecoveryContextSha256,
    string CommissioningPresetSha256,
    string TransformCalibrationId,
    string PierSide,
    string CoordinateEpoch,
    double SegmentOriginRaDegrees,
    double SegmentOriginDeclinationDegrees,
    double PriorReportedRaDegrees,
    double PriorReportedDeclinationDegrees,
    double CommandedRaDegrees,
    double CommandedDeclinationDegrees,
    double CommandMagnitudeDegrees,
    double PreMoveResidualPixels,
    double MaximumSingleCorrectionDegrees,
    double MaximumCumulativeCorrectionDegrees,
    int MaximumCorrectionAttempts,
    double MaximumAcquisitionSeconds,
    double CumulativeCorrectionDegrees,
    int CorrectionAttempts,
    DateTimeOffset FineAcquisitionStartedUtc,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    SlitPlacementPendingPhase Phase,
    string? MoveIntentEvidencePath = null,
    string? LastReason = null,
    bool TransformInvalidated = false,
    string? TransformInvalidReason = null)
{
    public const int CurrentSchemaVersion = 3;

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion) issues.Add($"Slit-placement pending schema must be {CurrentSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(ObservationRunId)) issues.Add("Observation run id is missing.");
        if (!Guid.TryParseExact(BudgetLineageId, "N", out _)) issues.Add("Budget lineage id must be a 32-character GUID in N format.");
        if (!IsSha256(ActionConfigurationSha256)) issues.Add("Action-configuration SHA-256 must be 64 hexadecimal characters.");
        if (!IsSha256(RecoveryContextSha256)) issues.Add("Recovery-context SHA-256 must be 64 hexadecimal characters.");
        if (!IsSha256(CommissioningPresetSha256)) issues.Add("Commissioning-preset SHA-256 must be 64 hexadecimal characters.");
        if (string.IsNullOrWhiteSpace(TransformCalibrationId)) issues.Add("Transform calibration id is missing.");
        if (string.IsNullOrWhiteSpace(PierSide) || PierSide.Contains("unknown", StringComparison.OrdinalIgnoreCase)) issues.Add("A known exact pier side is required.");
        if (string.IsNullOrWhiteSpace(CoordinateEpoch)) issues.Add("The mount coordinate epoch is missing.");
        ValidateRa(issues, SegmentOriginRaDegrees, "segment origin RA");
        ValidateDec(issues, SegmentOriginDeclinationDegrees, "segment origin Dec");
        ValidateRa(issues, PriorReportedRaDegrees, "prior reported RA");
        ValidateDec(issues, PriorReportedDeclinationDegrees, "prior reported Dec");
        ValidateRa(issues, CommandedRaDegrees, "commanded RA");
        ValidateDec(issues, CommandedDeclinationDegrees, "commanded Dec");
        if (!double.IsFinite(CommandMagnitudeDegrees) || CommandMagnitudeDegrees <= 0 ||
            CommandMagnitudeDegrees > MaximumSingleCorrectionDegrees + 1e-12)
        {
            issues.Add("Command magnitude is invalid or exceeds the single-motion limit.");
        }
        if (!double.IsFinite(PreMoveResidualPixels) || PreMoveResidualPixels <= 0) issues.Add("Pre-move residual is invalid.");
        if (!double.IsFinite(MaximumSingleCorrectionDegrees) || MaximumSingleCorrectionDegrees <= 0) issues.Add("Single-motion limit is invalid.");
        if (!double.IsFinite(MaximumCumulativeCorrectionDegrees) || MaximumCumulativeCorrectionDegrees < MaximumSingleCorrectionDegrees) issues.Add("Cumulative-motion limit is invalid.");
        if (MaximumCorrectionAttempts <= 0) issues.Add("Correction-attempt limit is invalid.");
        if (!double.IsFinite(MaximumAcquisitionSeconds) || MaximumAcquisitionSeconds <= 0) issues.Add("Maximum acquisition time is invalid.");
        if (!double.IsFinite(CumulativeCorrectionDegrees) || CumulativeCorrectionDegrees < 0 ||
            CumulativeCorrectionDegrees > MaximumCumulativeCorrectionDegrees + 1e-12)
        {
            issues.Add("Consumed cumulative correction is outside the declared limit.");
        }
        if (CorrectionAttempts < 0 || CorrectionAttempts > MaximumCorrectionAttempts) issues.Add("Consumed correction attempts are outside the declared limit.");
        if (CorrectionAttempts < 1) issues.Add("At least one conservative motion attempt must be recorded.");
        if (CumulativeCorrectionDegrees + 1e-12 < CommandMagnitudeDegrees) issues.Add("Consumed cumulative correction cannot be less than the declared command magnitude.");
        if (!Enum.IsDefined(Phase)) issues.Add("Pending-state phase is not defined.");
        if (TransformInvalidated && string.IsNullOrWhiteSpace(TransformInvalidReason)) issues.Add("Transform invalidation requires a durable reason.");
        if (FineAcquisitionStartedUtc == default || CreatedUtc < FineAcquisitionStartedUtc || UpdatedUtc < CreatedUtc) issues.Add("Pending-state timestamps are invalid.");
        return issues.AsReadOnly();
    }

    private static void ValidateRa(ICollection<string> issues, double value, string label)
    {
        if (!double.IsFinite(value) || value is < 0 or >= 360) issues.Add($"{label} must be finite in [0,360).");
    }

    private static void ValidateDec(ICollection<string> issues, double value, string label)
    {
        if (!double.IsFinite(value) || value is < -90 or > 90) issues.Add($"{label} must be finite in [-90,90].");
    }

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == 64 &&
        value.All(Uri.IsHexDigit);
}

public sealed record SlitPlacementReturnStep(
    GateResult Gate,
    bool AlreadyAtOrigin,
    double CurrentRadiusArcseconds,
    double CommandedRaDegrees,
    double CommandedDeclinationDegrees,
    double CommandMagnitudeDegrees);

public static class SlitPlacementRecoveryPlanner
{
    public static GateResult ValidateOutboundAndReturnReserve(
        MotionLimits limits,
        double cumulativeDegrees,
        int attempts,
        double segmentMagnitudeDegrees)
    {
        if (!double.IsFinite(cumulativeDegrees) || cumulativeDegrees < 0 ||
            cumulativeDegrees > limits.MaximumCumulativeCorrectionDegrees + 1e-12 ||
            attempts < 0 || attempts > limits.MaximumCorrectionAttempts)
        {
            return GateResult.Fail("SLIT_SEGMENT_BUDGET_INVALID", "Current fine-motion budget counters are invalid or outside the commissioned envelope.");
        }
        if (!double.IsFinite(segmentMagnitudeDegrees) || segmentMagnitudeDegrees <= 0 ||
            segmentMagnitudeDegrees > limits.MaximumSingleCorrectionDegrees + 1e-12)
        {
            return GateResult.Fail("SLIT_SEGMENT_SINGLE_LIMIT", "The proposed slit segment is invalid or exceeds the single-motion limit.");
        }
        // The same segment-sized distance is reserved for a failure return to
        // this segment's reported start coordinate.
        var requiredCumulative = cumulativeDegrees + 2 * segmentMagnitudeDegrees;
        if (requiredCumulative > limits.MaximumCumulativeCorrectionDegrees + 1e-12)
        {
            return GateResult.Fail(
                "SLIT_SEGMENT_RETURN_CUMULATIVE_RESERVE_LIMIT",
                $"The segment plus failure return requires {requiredCumulative * 3600:F2} arcsec cumulative, exceeding {limits.MaximumCumulativeCorrectionDegrees * 3600:F2} arcsec.");
        }
        if (attempts > limits.MaximumCorrectionAttempts - 2)
        {
            return GateResult.Fail(
                "SLIT_SEGMENT_RETURN_ATTEMPT_RESERVE_LIMIT",
                $"The segment plus one failure-return move requires more than the {limits.MaximumCorrectionAttempts - attempts} remaining action(s).");
        }
        return GateResult.Pass(
            "SLIT_SEGMENT_AND_RETURN_RESERVED",
            "The next closed-loop slit segment and one no-larger-than-single-limit failure return are reserved.");
    }

    public static SlitPlacementReturnStep PlanNextReturnStep(
        SlitPlacementPendingState state,
        double reportedRaDegrees,
        double reportedDeclinationDegrees,
        double arrivalToleranceArcseconds)
    {
        ArgumentNullException.ThrowIfNull(state);
        var issues = state.Validate();
        if (issues.Count > 0)
        {
            return Block("SLIT_PENDING_INVALID", string.Join(" ", issues));
        }
        if (!double.IsFinite(arrivalToleranceArcseconds) || arrivalToleranceArcseconds <= 0)
        {
            return Block("SLIT_RETURN_TOLERANCE_INVALID", "Arrival tolerance must be positive and finite.");
        }
        var (raOffset, decOffset) = SignedTangentOffsetArcseconds(
            state.SegmentOriginRaDegrees,
            state.SegmentOriginDeclinationDegrees,
            reportedRaDegrees,
            reportedDeclinationDegrees);
        var radius = Math.Sqrt(raOffset * raOffset + decOffset * decOffset);
        if (!double.IsFinite(radius)) return Block("SLIT_RETURN_POSITION_INVALID", "The reported origin offset is not finite.");
        if (radius <= arrivalToleranceArcseconds)
        {
            return new SlitPlacementReturnStep(
                GateResult.Pass("SLIT_SEGMENT_ORIGIN_REACHED", "The reported mount position is at the saved segment origin."),
                true,
                radius,
                state.SegmentOriginRaDegrees,
                state.SegmentOriginDeclinationDegrees,
                0);
        }

        var maximumSingleArcseconds = state.MaximumSingleCorrectionDegrees * 3600;
        // A segment command itself is no larger than single, so any larger
        // reported radius proves external/partial motion outside this recovery
        // authority and must never be auto-crossed.
        if (radius > maximumSingleArcseconds + arrivalToleranceArcseconds)
        {
            return Block(
                "SLIT_RETURN_OUTSIDE_SEGMENT_ENVELOPE",
                $"The reported position is {radius:F2} arcsec from the saved segment origin, outside the {maximumSingleArcseconds:F2} arcsec segment envelope.",
                radius);
        }
        var moveArcseconds = Math.Min(radius, maximumSingleArcseconds);
        var moveDegrees = moveArcseconds / 3600;
        if (state.CumulativeCorrectionDegrees + moveDegrees > state.MaximumCumulativeCorrectionDegrees + 1e-12)
        {
            return Block("SLIT_RETURN_CUMULATIVE_LIMIT", "The reported-position return no longer fits the durable cumulative-motion envelope.", radius);
        }
        if (state.CorrectionAttempts >= state.MaximumCorrectionAttempts)
        {
            return Block("SLIT_RETURN_ATTEMPT_LIMIT", "The reported-position return no longer fits the durable correction-attempt envelope.", radius);
        }

        var fractionRemaining = Math.Max(0, 1 - moveArcseconds / radius);
        var nextRaOffset = raOffset * fractionRemaining;
        var nextDecOffset = decOffset * fractionRemaining;
        var (nextRa, nextDec) = ApplyOffset(
            state.SegmentOriginRaDegrees,
            state.SegmentOriginDeclinationDegrees,
            nextRaOffset,
            nextDecOffset);
        if (!double.IsFinite(nextRa) || nextRa is < 0 or >= 360 ||
            !double.IsFinite(nextDec) || nextDec is < -90 or > 90)
        {
            return Block(
                "SLIT_RETURN_COORDINATE_SINGULAR",
                "The bounded return coordinate is singular or outside the celestial coordinate range.",
                radius);
        }
        return new SlitPlacementReturnStep(
            GateResult.Pass("SLIT_RETURN_STEP_BOUNDED", $"Bounded return step is {moveArcseconds:F2} arcsec."),
            false,
            radius,
            nextRa,
            nextDec,
            moveDegrees);
    }

    private static SlitPlacementReturnStep Block(string code, string message, double radius = double.NaN) =>
        new(GateResult.Unknown(code, message), false, radius, double.NaN, double.NaN, double.NaN);

    private static (double RaArcseconds, double DecArcseconds) SignedTangentOffsetArcseconds(
        double originRaDegrees,
        double originDecDegrees,
        double targetRaDegrees,
        double targetDecDegrees)
    {
        if (!double.IsFinite(targetRaDegrees) || targetRaDegrees is < 0 or >= 360 ||
            !double.IsFinite(targetDecDegrees) || targetDecDegrees is < -90 or > 90)
        {
            return (double.NaN, double.NaN);
        }
        var deltaRa = targetRaDegrees - originRaDegrees;
        if (deltaRa > 180) deltaRa -= 360;
        if (deltaRa < -180) deltaRa += 360;
        var referenceDec = (originDecDegrees + targetDecDegrees) * 0.5 * Math.PI / 180;
        return (deltaRa * Math.Cos(referenceDec) * 3600, (targetDecDegrees - originDecDegrees) * 3600);
    }

    private static (double RaDegrees, double DecDegrees) ApplyOffset(
        double originRaDegrees,
        double originDecDegrees,
        double raArcseconds,
        double decArcseconds)
    {
        var dec = originDecDegrees + decArcseconds / 3600;
        var cosDec = Math.Cos(originDecDegrees * Math.PI / 180);
        if (Math.Abs(cosDec) < 1e-6 || dec is < -90 or > 90) return (double.NaN, double.NaN);
        var ra = ((originRaDegrees + raArcseconds / (3600 * cosDec)) % 360 + 360) % 360;
        return (ra, dec);
    }
}

public sealed record SlitPlacementPendingLoadResult(
    SlitPlacementPendingState? State,
    string? Error);

public sealed record SlitPlacementPendingFileResult(
    string Path,
    SlitPlacementPendingState? State,
    string? Error);

/// <summary>
/// A validated pending file paired with the terminal status of its immutable
/// observation manifest. A terminal manifest closes a settled budget lineage,
/// but can never excuse an outstanding physical move.
/// </summary>
public sealed record SlitPlacementBudgetCandidate(
    string Path,
    SlitPlacementPendingState State,
    bool RunIsTerminal);

public sealed record SlitPlacementBudgetSelection(
    GateResult Gate,
    SlitPlacementBudgetCandidate? Candidate);

public static class SlitPlacementBudgetLineageResolver
{
    private const double BudgetToleranceDegrees = 1e-12;

    public static SlitPlacementBudgetSelection Resolve(
        IReadOnlyList<SlitPlacementBudgetCandidate> candidates,
        string currentRunId)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRunId);
        if (candidates.Count == 0)
        {
            return Pass(null, "SLIT_BUDGET_LINEAGE_EMPTY", "No durable slit-placement budget lineage is active.");
        }

        var outstanding = candidates
            .Where(candidate => candidate.State.Phase != SlitPlacementPendingPhase.SettledBudgetLedger)
            .ToArray();
        if (outstanding.Length > 1)
        {
            return Block(
                "SLIT_PENDING_MULTIPLE_OUTSTANDING",
                $"{outstanding.Length} durable slit moves remain outstanding; automatic motion is prohibited until explicit manual takeover resolves the ambiguity.");
        }
        if (outstanding.Length == 1 && outstanding[0].RunIsTerminal)
        {
            return Block(
                "SLIT_PENDING_TERMINAL_RUN_OUTSTANDING",
                "A terminal observation manifest still has an outstanding slit move. Automatic adoption is prohibited because terminal state and physical recovery authority conflict.");
        }

        var activeGroups = candidates
            .GroupBy(candidate => candidate.State.BudgetLineageId, StringComparer.Ordinal)
            .Where(group =>
                group.Any(candidate => candidate.State.Phase != SlitPlacementPendingPhase.SettledBudgetLedger) ||
                !group.Any(candidate => candidate.RunIsTerminal))
            .ToArray();
        if (activeGroups.Length > 1)
        {
            return Block(
                "SLIT_BUDGET_MULTIPLE_ACTIVE_LINEAGES",
                $"{activeGroups.Length} non-terminal slit-placement budget lineages are active. Their movement budgets cannot be merged automatically.");
        }
        if (activeGroups.Length == 0)
        {
            return Pass(null, "SLIT_BUDGET_LINEAGES_CLOSED", "All settled slit-placement budget lineages belong to terminal observation runs.");
        }

        var groupCandidates = activeGroups[0].ToArray();
        if (!HaveConsistentBindings(groupCandidates))
        {
            return Block(
                "SLIT_BUDGET_LINEAGE_BINDINGS_DIVERGED",
                "Durable files in the active slit-placement budget lineage disagree about configuration, commissioning, transform, pier side, coordinate epoch or motion limits.");
        }
        if (!HaveConsistentFineStart(groupCandidates))
        {
            return Block(
                "SLIT_BUDGET_LINEAGE_TIME_DIVERGED",
                "Durable files in the active slit-placement budget lineage disagree about the fine-acquisition start time.");
        }
        var dominant = FindDominantCandidate(groupCandidates);
        if (dominant is null)
        {
            return Block(
                "SLIT_BUDGET_LINEAGE_COUNTERS_DIVERGED",
                "Durable files in the active slit-placement budget lineage have divergent cumulative-distance or attempt counters.");
        }

        if (outstanding.Length == 1)
        {
            var pending = outstanding[0];
            if (!string.Equals(
                    pending.State.BudgetLineageId,
                    dominant.State.BudgetLineageId,
                    StringComparison.Ordinal) ||
                !Dominates(pending.State, dominant.State))
            {
                return Block(
                    "SLIT_PENDING_BUDGET_NOT_MONOTONIC",
                    "The outstanding slit move does not conservatively include every consumed action in its durable budget lineage.");
            }
            return Pass(
                pending,
                "SLIT_PENDING_OUTSTANDING_SELECTED",
                "The unique non-terminal outstanding move conservatively dominates its durable budget lineage.");
        }

        var current = groupCandidates
            .Where(candidate => string.Equals(candidate.State.ObservationRunId, currentRunId, StringComparison.Ordinal))
            .Where(candidate => Dominates(candidate.State, dominant.State))
            .OrderByDescending(candidate => candidate.State.UpdatedUtc)
            .FirstOrDefault();
        return Pass(
            current ?? dominant,
            "SLIT_BUDGET_LINEAGE_SELECTED",
            "The unique non-terminal slit-placement budget lineage was selected without resetting its counters.");
    }

    private static bool HaveConsistentFineStart(IReadOnlyList<SlitPlacementBudgetCandidate> candidates) =>
        candidates.All(candidate =>
            candidate.State.FineAcquisitionStartedUtc == candidates[0].State.FineAcquisitionStartedUtc);

    private static bool HaveConsistentBindings(IReadOnlyList<SlitPlacementBudgetCandidate> candidates)
    {
        var first = candidates[0].State;
        return candidates.All(candidate =>
        {
            var state = candidate.State;
            return string.Equals(state.ActionConfigurationSha256, first.ActionConfigurationSha256, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(state.RecoveryContextSha256, first.RecoveryContextSha256, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(state.CommissioningPresetSha256, first.CommissioningPresetSha256, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(state.TransformCalibrationId, first.TransformCalibrationId, StringComparison.Ordinal) &&
                   string.Equals(state.PierSide, first.PierSide, StringComparison.Ordinal) &&
                   string.Equals(state.CoordinateEpoch, first.CoordinateEpoch, StringComparison.Ordinal) &&
                   Math.Abs(state.MaximumSingleCorrectionDegrees - first.MaximumSingleCorrectionDegrees) <= BudgetToleranceDegrees &&
                   Math.Abs(state.MaximumCumulativeCorrectionDegrees - first.MaximumCumulativeCorrectionDegrees) <= BudgetToleranceDegrees &&
                   state.MaximumCorrectionAttempts == first.MaximumCorrectionAttempts &&
                   Math.Abs(state.MaximumAcquisitionSeconds - first.MaximumAcquisitionSeconds) <= 1e-6;
        });
    }

    private static SlitPlacementBudgetCandidate? FindDominantCandidate(
        IReadOnlyList<SlitPlacementBudgetCandidate> candidates) =>
        candidates
            .Where(candidate => candidates.All(other => Dominates(candidate.State, other.State)))
            .OrderByDescending(candidate => candidate.State.UpdatedUtc)
            .FirstOrDefault();

    private static bool Dominates(SlitPlacementPendingState candidate, SlitPlacementPendingState other) =>
        candidate.CumulativeCorrectionDegrees + BudgetToleranceDegrees >= other.CumulativeCorrectionDegrees &&
        candidate.CorrectionAttempts >= other.CorrectionAttempts;

    private static SlitPlacementBudgetSelection Pass(
        SlitPlacementBudgetCandidate? candidate,
        string code,
        string message) =>
        new(GateResult.Pass(code, message), candidate);

    private static SlitPlacementBudgetSelection Block(string code, string message) =>
        new(GateResult.Unknown(code, message), null);
}

public static class SlitPlacementPendingStore
{
    private const int EnvelopeSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task WriteAtomicAsync(
        string path,
        SlitPlacementPendingState state,
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
            var envelope = new SlitPlacementPendingEnvelope(
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

    public static async Task<SlitPlacementPendingLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) return new SlitPlacementPendingLoadResult(null, null);
        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1 << 14,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var envelope = await JsonSerializer.DeserializeAsync<SlitPlacementPendingEnvelope>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (envelope is null) return new SlitPlacementPendingLoadResult(null, "The pending file contains JSON null.");
            if (envelope.EnvelopeSchemaVersion != EnvelopeSchemaVersion)
            {
                return new SlitPlacementPendingLoadResult(null, $"Pending envelope schema must be {EnvelopeSchemaVersion}.");
            }
            if (envelope.State is null || !IsSha256(envelope.StateSha256))
            {
                return new SlitPlacementPendingLoadResult(null, "Pending envelope state or SHA-256 is missing.");
            }
            var canonicalState = JsonSerializer.SerializeToUtf8Bytes(envelope.State, JsonOptions);
            var actualSha256 = SHA256.HashData(canonicalState);
            var expectedSha256 = Convert.FromHexString(envelope.StateSha256);
            if (!CryptographicOperations.FixedTimeEquals(actualSha256, expectedSha256))
            {
                return new SlitPlacementPendingLoadResult(null, "Pending envelope state SHA-256 does not match its canonical payload.");
            }
            var state = envelope.State;
            var issues = state.Validate();
            return issues.Count == 0
                ? new SlitPlacementPendingLoadResult(state, null)
                : new SlitPlacementPendingLoadResult(null, string.Join(" ", issues));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SlitPlacementPendingLoadResult(null, ex.Message);
        }
    }

    /// <summary>
    /// Finds the one well-known pending file directly below each observation
    /// run directory. This deliberately avoids recursive traversal: a process
    /// restart must discover old run-bound motion authority, while unrelated
    /// files and directory junctions must not expand the search surface.
    /// </summary>
    public static async Task<IReadOnlyList<SlitPlacementPendingFileResult>> DiscoverAsync(
        string observationsRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationsRoot);
        var root = Path.GetFullPath(observationsRoot);
        if (!Directory.Exists(root)) return Array.Empty<SlitPlacementPendingFileResult>();

        string[] runDirectories;
        try
        {
            runDirectories = Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [new SlitPlacementPendingFileResult(root, null, ex.Message)];
        }

        var results = new List<SlitPlacementPendingFileResult>();
        foreach (var runDirectory in runDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(runDirectory, "control", "slit-placement-pending.json");
            if (!File.Exists(path)) continue;
            var loaded = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
            results.Add(new SlitPlacementPendingFileResult(Path.GetFullPath(path), loaded.State, loaded.Error));
        }
        return results.AsReadOnly();
    }

    public static void DeleteIfExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.Delete(Path.GetFullPath(path));
    }

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed record SlitPlacementPendingEnvelope(
        int EnvelopeSchemaVersion,
        string StateSha256,
        SlitPlacementPendingState State);
}
