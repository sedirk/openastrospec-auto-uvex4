using System.IO;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using NINA.Image.Interfaces;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal enum G3PlateSolveTransientOperation
{
    Capture,
    FitsRead,
    Solver,
}

internal sealed partial class RealObservationStageRunner
{
    internal static GateResult ClassifyG3OverexposedLadder(
        IReadOnlyList<G3PlateSolveAttemptEvidence> attempts, GateResult? contentGate)
    {
        // Clipping ends this acquisition, not the night. Classify only a
        // completed, validated optical ladder: a clipped image must not hide
        // a capture/solver fault, missing provenance or a changed owner.
        // The independent terminal stop/return/retirement checks still decide
        // whether the saved native policy may execute another target.
        var validClipping = contentGate is { Code: G3SolveProbeContentAnalyzer.OverexposedGateCode,
                Disposition: GateDisposition.Indeterminate } && attempts.Count > 0 &&
            attempts.Select(a => a.LadderIndex).SequenceEqual(Enumerable.Range(1, attempts.Count)) &&
            attempts.All(a => !a.SolveSucceeded && a.Disposition == GateDisposition.Indeterminate &&
                a.ExposureMilliseconds > 0 && !string.IsNullOrWhiteSpace(a.FramePath) &&
                !string.IsNullOrWhiteSpace(a.AttemptEvidencePath) && a.MountBinding is { } binding &&
                binding.FramePath == a.FramePath && !string.IsNullOrWhiteSpace(binding.FrameSha256) &&
                (a.GateCode == "G3_PLATE_SOLVE_TIER_FAILED" && a.ContentGateCode is
                    "G3_SOLVE_PROBE_STRUCTURED_FIELD" or "G3_CLOUD_OR_TRANSPARENCY_INVALID" ||
                 a.GateCode == G3SolveProbeContentAnalyzer.OverexposedGateCode &&
                 a.ContentGateCode == G3SolveProbeContentAnalyzer.OverexposedGateCode)) &&
            attempts[^1].GateCode == G3SolveProbeContentAnalyzer.OverexposedGateCode;
        return validClipping
            ? GateResult.TargetQuality(G3SolveProbeContentAnalyzer.OverexposedGateCode,
                "The validated G3 solve ladder reached widespread detector clipping without a formal solve. " +
                "Stop longer exposures and neighbouring-field search; this target-local optical failure may " +
                "follow the saved skip policy only after independent stopped/return reconciliation. " +
                "The clipped frame grants no target identity, search motion or additional budget.", contentGate!.Metrics)
            : GateResult.Unknown(G3SolveProbeContentAnalyzer.OverexposedGateCode,
                "The G3 solve ladder included clipping but lacks complete validated optical-failure evidence. " +
                "Capture, solver or binding uncertainty cannot be converted to a target-quality skip. " +
                "Longer exposures and neighbouring-field search remain prohibited.", contentGate?.Metrics);
    }

    internal static GateResult PreserveG3OverexposureFallbackFailure(GateResult fallbackGate) =>
        // A deterministic short-frame fallback can itself discover a real
        // safety/owner failure. Keep its original code, scope and metrics;
        // an earlier optical failure cannot launder it into a skippable one.
        fallbackGate with { Message = "The G3 solve probe was overexposed; fresh deterministic analysis also failed. " +
            "No neighbouring-field search or motion prediction is authorized from the clipped probe. " + fallbackGate.Message };

    internal static GateResult ClassifyG3EmptyLadder(
        IReadOnlyList<G3PlateSolveAttemptEvidence> attempts, int expectedTiers)
    {
        // Only a complete ladder of validated pixels + ordinary failed solves
        // supports an optical no-stars conclusion. A valid empty image cannot
        // launder an invalid capture/identity/buffer from another tier.
        var validEmpty = expectedTiers > 0 && attempts.Count == expectedTiers &&
            attempts.Select(a => a.LadderIndex).SequenceEqual(Enumerable.Range(1, expectedTiers)) &&
            attempts.All(a => !a.SolveSucceeded && a.GateCode == "G3_PLATE_SOLVE_TIER_FAILED" &&
                a.ContentGateCode == "G3_CLOUD_OR_TRANSPARENCY_INVALID" && a.CoherentSourceCount == 0 &&
                a.MountBinding is not null && !string.IsNullOrWhiteSpace(a.FramePath));
        return validEmpty
            ? GateResult.TargetQuality("G3_CLOUD_OR_TRANSPARENCY_INVALID",
                "Validated G3 frames and completed solves found no coherent source. Keep the bounded stationary retries; after exhaustion this is a target-local optical failure, not a weather-monitor verdict. No search motion is authorized.")
            : GateResult.Unknown("G3_PLATE_SOLVE_PIXEL_EVIDENCE_INVALID",
                "The exhausted G3 ladder lacks complete validated empty-field evidence; invalid pixels/capture/solver evidence cannot be classified as cloud or target quality. No search motion is authorized.");
    }

    /// <summary>
    /// Classifies only failures for which abandoning the current disposable
    /// evidence path and advancing the commissioned exposure ladder is safe.
    /// Cancellation, identity/profile drift, hash/provenance failures and
    /// configuration failures deliberately remain exceptions and reach the
    /// ordinary hard-stop path.
    /// </summary>
    internal static GateResult? ClassifyG3PlateSolveTransientFailure(
        G3PlateSolveTransientOperation operation,
        Exception exception)
    {
        if (exception is OperationCanceledException or
            UnauthorizedAccessException or
            SecurityException or
            CryptographicException or
            Phd2IdentityMismatchException or
            Phd2DisconnectedException or
            PhysicalActionGateException)
        {
            return null;
        }

        var message = exception.Message ?? string.Empty;
        if (ContainsHardEvidenceTerm(message))
        {
            return null;
        }

        var recoverable = operation switch
        {
            G3PlateSolveTransientOperation.Capture =>
                exception is Phd2CommandTimeoutException or IOException ||
                exception is Phd2CaptureException && IsRecoverableCaptureMessage(message),
            G3PlateSolveTransientOperation.FitsRead =>
                exception is IOException or InvalidDataException,
            G3PlateSolveTransientOperation.Solver =>
                exception is TimeoutException or IOException or HttpRequestException or ObjectDisposedException,
            _ => false,
        };
        if (!recoverable)
        {
            return null;
        }

        var (code, label) = operation switch
        {
            G3PlateSolveTransientOperation.Capture =>
                ("G3_PLATE_SOLVE_CAPTURE_TRANSIENT_TIER_SKIPPED", "PHD2/G3 capture"),
            G3PlateSolveTransientOperation.FitsRead =>
                ("G3_PLATE_SOLVE_FITS_READ_TRANSIENT_TIER_SKIPPED", "fresh G3 FITS read"),
            G3PlateSolveTransientOperation.Solver =>
                ("G3_PLATE_SOLVE_SOLVER_TRANSIENT_TIER_SKIPPED", "configured G3 solver"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        return GateResult.Unknown(
            code,
            $"A recoverable {label} failure invalidated only this exposure tier: {message} " +
            "The reserved evidence path will not be reused; the next commissioned tier must capture a new immutable frame.");
    }

    private static bool IsRecoverableCaptureMessage(string message) =>
        message.Contains("did not become readable", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("native single-frame capture failed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Could not copy PHD2 saved image", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsHardEvidenceTerm(string message) =>
        message.Contains("identity", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("profile", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("commission", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("hash", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("sha-256", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("checksum", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("optical parameters", StringComparison.OrdinalIgnoreCase);

    private static bool IsG3PlateSolveTransientGateCode(string code) =>
        code is "G3_PLATE_SOLVE_CAPTURE_TRANSIENT_TIER_SKIPPED" or
            "G3_PLATE_SOLVE_FITS_READ_TRANSIENT_TIER_SKIPPED" or
            "G3_PLATE_SOLVE_SOLVER_TRANSIENT_TIER_SKIPPED";

    private async Task<G3PlateSolveProbeState> RecordG3PlateSolveTransientFailureAsync(
        int presetSchemaVersion,
        string presetId,
        int ladderIndex,
        int exposureMilliseconds,
        string reservedFramePath,
        G3PlateSolveTransientOperation operation,
        Exception exception,
        GateResult gate,
        List<G3PlateSolveAttemptEvidence> attempts,
        G3FrameMountReadback beforeExposureMountReadback,
        G3FieldMountBinding? mountBinding,
        IImageData? image,
        G3SolveProbeContentAssessment? content,
        string? frameSha256,
        CancellationToken cancellationToken)
    {
        // Do not bind the JSON to sourcePath here. Capture/FITS failures can
        // leave a missing or temporarily unreadable file, and re-opening it to
        // publish diagnostics would turn the reviewed recovery back into a
        // top-level exception. The reserved path and any already-computed hash
        // are retained as values; the failed frame is never promoted.
        var attemptPath = await PublishRunJsonEvidenceAsync(
            "g3-plate-solve-ladder-transient-attempt",
            $"G3 plate-solve transient attempt at exposure ladder tier {ladderIndex}",
            new
            {
                presetSchemaVersion,
                presetId,
                ladderIndex,
                exposureMilliseconds,
                operation = operation.ToString(),
                disposition = gate.Disposition.ToString(),
                gate.Code,
                gate.Message,
                exceptionType = exception.GetType().FullName,
                exceptionMessage = exception.Message,
                reservedFramePath,
                frameExists = File.Exists(reservedFramePath),
                frameSha256,
                mountBinding,
                nextRecovery = "AdvanceToNextCommissionedExposureTierWithNewImmutablePath",
                samePathRetryAuthorized = false,
                motionAuthorized = false,
                durableMotionBudgetsReset = false,
            },
            sourcePath: null,
            cancellationToken).ConfigureAwait(false);

        attempts.Add(new G3PlateSolveAttemptEvidence(
            ladderIndex,
            exposureMilliseconds,
            gate.Code,
            gate.Disposition,
            SolveSucceeded: false,
            reservedFramePath,
            attemptPath,
            ContentGateCode: content?.Gate.Code,
            CoherentSourceCount: content?.StellarMeasurement.DetectedStarCount ?? 0,
            MountBinding: mountBinding));
        await WriteAuditBestEffortAsync("g3-plate-solve-tier-transient-skipped", new
        {
            presetId,
            ladderIndex,
            exposureMilliseconds,
            operation = operation.ToString(),
            gate.Code,
            exceptionType = exception.GetType().FullName,
            reservedFramePath,
            nextTier = ladderIndex + 1,
            samePathRetryAuthorized = false,
            motionAuthorized = false,
            durableMotionBudgetsReset = false,
        }).ConfigureAwait(false);

        return new G3PlateSolveProbeState(
            gate,
            reservedFramePath,
            image,
            Solve: null,
            content,
            attempts.AsReadOnly(),
            MountBinding: mountBinding,
            BeforeExposureMountReadback: beforeExposureMountReadback);
    }
}
