using System.Globalization;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>Display telemetry only; never authorizes acquisition or changes the safety time budget.</summary>
public sealed record ObservationAcquisitionProgress(
    string ObservationRunId, int RequestedFrames, int AcceptedFrames, int AttemptedFrames,
    int MaximumAttempts, int ReusedProbeFrames, double AcceptedExposureSeconds,
    double? SelectedExposureSeconds, string Phase = "Ready", string Role = "",
    string? CaptureId = null, DateTimeOffset? CaptureStartedUtc = null, double CaptureExposureSeconds = 0);

public sealed record ObservationAcquisitionPresentation(
    bool Visible, string Summary, string Timing, double BlockPercent, double FramePercent,
    bool FrameIndeterminate, int RemainingFrames, double? RemainingExposureSeconds)
{
    public static ObservationAcquisitionPresentation Empty { get; } = new(false, "", "", 0, 0, false, 0, null);

    public static ObservationAcquisitionPresentation Build(ObservationAcquisitionProgress? p,
        string? runId, ObservationRunState state, DateTimeOffset now, CultureInfo culture)
    {
        if (p is null || p.ObservationRunId != runId || p.RequestedFrames <= 0) return Empty;
        var en = !culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        var remaining = Math.Max(0, p.RequestedFrames - p.AcceptedFrames);
        var active = state == ObservationRunState.RunningAuto;
        var capturing = active && p.Phase == "Exposing" && p.CaptureStartedUtc is not null;
        var processing = active && p.Phase == "Processing";
        var elapsed = capturing ? Math.Clamp((now - p.CaptureStartedUtc!.Value).TotalSeconds, 0,
            Math.Max(0, p.CaptureExposureSeconds)) : 0;
        var frameRemaining = Math.Max(0, p.CaptureExposureSeconds - elapsed);
        double? eta = active && p.SelectedExposureSeconds is > 0
            ? remaining * p.SelectedExposureSeconds.Value : null;
        if (eta.HasValue && p.Role == "SCIENCE")
            eta = Math.Max(0, eta.Value - (processing ? p.CaptureExposureSeconds : elapsed));
        var waiting = !active ? (state == ObservationRunState.Completed ? (en ? "Finished" : "已结束") :
            (en ? "Not advancing; ETA suspended" : "未推进，剩余时间暂停估算")) :
            processing ? (en ? "Reading / saving / quality check" : "读图／保存／质量检查中") :
            capturing ? (p.Role == "PROBE" ? (en ? "Trial exposure" : "试拍选档") :
                (en ? $"Science frame {Math.Min(p.RequestedFrames, p.AcceptedFrames + 1)}/{p.RequestedFrames}" :
                    $"正在拍第 {Math.Min(p.RequestedFrames, p.AcceptedFrames + 1)}/{p.RequestedFrames} 张科学帧")) :
            (en ? "Waiting for next frame / finalization" : "等待下一帧／收尾");
        var summary = en
            ? $"{waiting} · Accepted {p.AcceptedFrames}/{p.RequestedFrames} · Remaining {remaining} (includes current) · Reused trials {p.ReusedProbeFrames}"
            : $"{waiting} · 已接受 {p.AcceptedFrames}/{p.RequestedFrames} · 剩余 {remaining} 张（含当前）· 复用试拍 {p.ReusedProbeFrames} 张";
        var current = capturing ? (en ? $"Current ~{Duration(frameRemaining)} / {Duration(p.CaptureExposureSeconds)}" :
            $"本张约剩 {Duration(frameRemaining)} / {Duration(p.CaptureExposureSeconds)}") : waiting;
        var timing = en
            ? $"{current} · Remaining integration ~{(eta.HasValue ? Duration(eta.Value) : "—")} · Accepted {Duration(p.AcceptedExposureSeconds)} · Attempts {p.AttemptedFrames}/{p.MaximumAttempts}; excludes overhead / retries"
            : $"{current} · 剩余纯曝光约 {(eta.HasValue ? Duration(eta.Value) : "—")} · 已接受积分 {Duration(p.AcceptedExposureSeconds)} · 科学预算 {p.AttemptedFrames}/{p.MaximumAttempts}；不含读存、复核及重试";
        // Estimated shutter time reaching zero is not proof of a saved or accepted file.
        var percent = capturing && p.CaptureExposureSeconds > 0 ? Math.Min(99, elapsed / p.CaptureExposureSeconds * 100) : 0;
        return new(true, summary, timing, Math.Clamp(100d * p.AcceptedFrames / p.RequestedFrames, 0, 100),
            percent, processing || (capturing && frameRemaining <= 0), remaining, eta);
    }

    private static string Duration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "—";
        var t = TimeSpan.FromSeconds(Math.Ceiling(Math.Min(seconds, 31536000)));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
