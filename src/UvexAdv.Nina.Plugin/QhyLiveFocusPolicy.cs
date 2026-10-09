using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Nina.Plugin;

/// <summary>Read-only image-quality evidence. This never refreshes WCS or grants focus/mount motion.</summary>
internal static class QhyLiveFocusPolicy
{
    internal static LiveFocusMetricState? FromFrame(QhyFrameRecord frame, string cameraId)
    {
        if (frame.Metrics.MedianFwhmPixels is not { } fwhm || !double.IsFinite(fwhm) || fwhm <= 0 ||
            frame.Metrics.QualityFlags.Count != 0 || frame.Sha256?.Length != 64 ||
            !frame.Sha256.All(Uri.IsHexDigit) || frame.ExposureEndedUtc < frame.ExposureStartedUtc)
            return null;

        return new(new(FocusMetricKind.QhyStellarShapeAndPlateSolve, cameraId,
                fwhm, "FWHM pixels", frame.Sha256),
            frame.ExposureEndedUtc, frame.ExposureEndedUtc.AddMinutes(30), GateDisposition.Passed);
    }

    internal static LiveFocusMetricState? RefreshFromPhotometry(
        QhyJobSnapshot snapshot, Guid jobId, string runId, string cameraId,
        LiveFocusMetricState? previous, DateTimeOffset now)
    {
        // Do not adopt a different run/owner, override an operator, or renew a
        // timestamp merely because a cached snapshot was polled again.
        if (snapshot.Id != jobId || snapshot.Kind != QhyJobKind.Photometry ||
            snapshot.ObservationRunId != runId || snapshot.ExpectedCameraStableId != cameraId ||
            snapshot.OperatorInterventionRequired)
            return previous;

        var latest = snapshot.Frames
            .Where(frame => (frame.Role == "PHOTOMETRY" || frame.Role.StartsWith("PHOTOMETRY-", StringComparison.Ordinal)) &&
                frame.Metrics.DetectedStars > 0 &&
                frame.ExposureStartedUtc >= snapshot.CreatedUtc && frame.ExposureEndedUtc <= now)
            .Select(frame => FromFrame(frame, cameraId))
            .Where(metric => metric is not null && metric.ValidUntilUtc >= now)
            .OrderByDescending(metric => metric!.VerifiedUtc)
            .FirstOrDefault();
        return latest is not null && (previous is null || latest.VerifiedUtc > previous.VerifiedUtc)
            ? latest : previous;
    }

    internal static bool IsOptionalLiveSetupGate(GateResult gate) =>
        gate.Code.StartsWith("QHY_", StringComparison.Ordinal) ||
        gate.Code.StartsWith("FOCUS_GS350_WIDE_FIELD_", StringComparison.Ordinal);

    internal static GateResult? Degradation(IEnumerable<GateResult> gates)
    {
        var failures = gates.Where(gate => gate.Disposition != GateDisposition.Passed &&
            IsOptionalLiveSetupGate(gate)).ToArray();
        if (failures.Length == 0) return null;
        return GateResult.Warn("QHY_LIVE_SETUP_DEGRADED",
            "测光支路的实时设备/焦质证据未通过，已停止使用该支路的质量与定位结论，不启动新作业；" +
            "原帧和现有作业收尾句柄保留，不宣称现有采集已停止。光谱链独立复核，未放宽导星、入缝、温控或安全门。 " +
            string.Join(" ", failures.Select(gate => $"{gate.Code}: {gate.Message}")));
    }
}
