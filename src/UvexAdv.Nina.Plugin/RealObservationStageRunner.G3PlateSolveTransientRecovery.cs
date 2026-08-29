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
