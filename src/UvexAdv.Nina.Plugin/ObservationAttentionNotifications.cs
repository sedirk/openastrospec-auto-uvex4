using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using UvexAdv.Observatory;
using Forms = System.Windows.Forms;

namespace UvexAdv.Nina.Plugin;

// This assembly is a N.I.N.A. Windows desktop plugin. The project intentionally
// targets net8.0-windows without a minimum OS version because N.I.N.A.'s own
// references do the same; NotifyIcon is nevertheless called only in that
// Windows-hosted process.
#pragma warning disable CA1416

internal enum ObservationAttentionSeverity
{
    Success,
    Warning,
    Error,
}

internal sealed record ObservationAttentionNotification(
    ObservationAttentionSeverity Severity,
    string Title,
    string Body,
    string Fingerprint);

internal sealed record ObservationAttentionNotificationEvaluation(
    ObservationAttentionNotification? Notification,
    bool ClearActiveIndicator);

internal sealed record ObservationCompletionNotificationContext(
    string ObservationRunId,
    string TargetName,
    long AcceptedScienceFrames,
    string Adapter);

/// <summary>
/// Converts coordinator terminal/attention states into one operator alert per
/// distinct blocker.  Returning to any non-alert state rearms the tracker, so
/// the same blocker is reported again if it recurs after Resume/revalidation.
/// Successful completion is deduplicated by run, independently of blocker recovery.
/// </summary>
internal sealed class ObservationAttentionNotificationTracker
{
    private string? activeFingerprint;
    private readonly HashSet<string> completedRuns = new(StringComparer.Ordinal);
    private readonly HashSet<string> nativeBoundaries = new(StringComparer.Ordinal);

    public ObservationAttentionNotificationEvaluation Evaluate(
        ObservationSnapshot snapshot,
        GateResult? currentGate,
        CultureInfo? culture = null,
        ObservationCompletionNotificationContext? completion = null,
        string? nativeTargetRunId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // The shared coordinator publishes Completed only after its durable
        // completion commit. Neither a passed science gate nor Finalizing is success.
        if (snapshot.State == ObservationRunState.Completed &&
            !string.IsNullOrWhiteSpace(snapshot.ObservationRunId))
        {
            if (!completedRuns.Add(snapshot.ObservationRunId))
                return new ObservationAttentionNotificationEvaluation(null, false);

            var ui = culture ?? CultureInfo.CurrentUICulture;
            var matching = completion?.ObservationRunId == snapshot.ObservationRunId ? completion : null;
            var simulated = string.Equals(matching?.Adapter, "simulator", StringComparison.Ordinal);
            var completionTitle = simulated
                ? ObservationUiPresentation.Text("OpenAstroSpec 模拟目标观测完成", "OpenAstroSpec simulated target completed", ui)
                : ObservationUiPresentation.Text("OpenAstroSpec 目标观测完成", "OpenAstroSpec target observation completed", ui);
            var target = string.IsNullOrWhiteSpace(matching?.TargetName)
                ? ObservationUiPresentation.Text("未提供目标名称", "Target name unavailable", ui)
                : matching.TargetName;
            var frames = matching is null
                ? ObservationUiPresentation.Text("科学帧计数未提供。", "Science frame count unavailable.", ui)
                : simulated
                    ? ObservationUiPresentation.Text("模拟流程，未采集真实科学帧。", "Simulation; no real science frames acquired.", ui)
                    : ObservationUiPresentation.Text($"已接受科学帧：{matching.AcceptedScienceFrames} 张。", $"Accepted science frames: {matching.AcceptedScienceFrames}.", ui);
            var completionBody = ObservationUiPresentation.Text(
                $"目标：{target}\n{frames}\n本目标流程和完成记录已保存。整夜序列与设备收口状态请查看运行报告。\n运行：{snapshot.ObservationRunId}",
                $"Target: {target}\n{frames}\nTarget workflow and completion record saved. See the run report for night sequence and equipment shutdown status.\nRun: {snapshot.ObservationRunId}",
                ui);
            activeFingerprint = $"{snapshot.ObservationRunId}|Completed";
            return new ObservationAttentionNotificationEvaluation(
                new ObservationAttentionNotification(ObservationAttentionSeverity.Success, completionTitle, completionBody, activeFingerprint), false);
        }

        if (snapshot.State is not (ObservationRunState.PausedNeedsAttention or ObservationRunState.Faulted))
        {
            var clear = activeFingerprint is not null;
            activeFingerprint = null;
            return new ObservationAttentionNotificationEvaluation(null, clear);
        }

        var gate = currentGate is { Disposition: not GateDisposition.Passed }
            ? currentGate
            : null;
        var code = gate?.Code ?? ResolveCoordinatorCode(snapshot);
        var reason = FirstNonBlank(
            gate?.Message,
            snapshot.PauseReason,
            snapshot.StatusMessage,
            "自动观测已停止，原因未提供。 ");
        var stage = snapshot.CurrentStage;
        var uiCulture = culture ?? CultureInfo.CurrentUICulture;
        var stageLabel = stage is null
            ? ObservationUiPresentation.Text("未标明阶段", "Unspecified stage", uiCulture)
            : ObservationUiPresentation.StageName(stage.Value, uiCulture);
        var fingerprint = string.Join(
            "|",
            snapshot.ObservationRunId ?? "no-run",
            snapshot.State,
            stage?.ToString() ?? "no-stage",
            code,
            reason);

        if (string.Equals(activeFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new ObservationAttentionNotificationEvaluation(null, false);
        }

        activeFingerprint = fingerprint;
        var nativeTarget = !string.IsNullOrWhiteSpace(nativeTargetRunId) &&
            nativeTargetRunId == snapshot.ObservationRunId;
        // A quality gate is not the night outcome. Let the native container
        // confirm stop/cleanup and save its skip-or-stop decision first.
        if (nativeTarget && snapshot.State == ObservationRunState.Faulted &&
            (gate is not null ? NativeSequencePlan.IsSkippableQualityFailure(gate) :
                NativeSequencePlan.IsSkippableQualityFailure(code)))
            return new ObservationAttentionNotificationEvaluation(null, false);
        var severity = snapshot.State == ObservationRunState.Faulted
            ? ObservationAttentionSeverity.Error
            : ObservationAttentionSeverity.Warning;
        var title = severity == ObservationAttentionSeverity.Error
            ? nativeTarget
                ? ObservationUiPresentation.Text("OpenAstroSpec 本目标观测失败", "OpenAstroSpec target observation failed", uiCulture)
                : ObservationUiPresentation.Text("OpenAstroSpec 自动观测发生故障", "OpenAstroSpec automation fault", uiCulture)
            : ObservationUiPresentation.Text("OpenAstroSpec 自动观测需要处理", "OpenAstroSpec automation needs attention", uiCulture);
        var diagnosticGate = gate ?? GateResult.Unknown(code, reason);
        var presentation = ObservationUiPresentation.Present(
            stage ?? ObservationStage.ValidateNightSetup,
            diagnosticGate,
            uiCulture);
        var body = ObservationUiPresentation.Text(
            $"阶段：{stageLabel}\n代码：{code}\n{presentation.Summary}\n下一步：打开“诊断与证据”查看影响、自动处理和证据。",
            $"Stage: {stageLabel}\nCode: {code}\n{presentation.Summary}\nNext: open Diagnostics and evidence for impact, recovery and evidence.",
            uiCulture);
        return new ObservationAttentionNotificationEvaluation(
            new ObservationAttentionNotification(severity, title, body, fingerprint),
            false);
    }

    public ObservationAttentionNotification? NativeBoundary(string runId, string target, string code,
        bool skipped, string? boundaryFailure, CultureInfo? culture = null)
    {
        if (string.IsNullOrWhiteSpace(runId) || !nativeBoundaries.Add(runId)) return null;
        var ui = culture ?? CultureInfo.CurrentUICulture;
        return new(skipped ? ObservationAttentionSeverity.Warning : ObservationAttentionSeverity.Error,
            skipped
                ? ObservationUiPresentation.Text("OpenAstroSpec 本目标已跳过", "OpenAstroSpec target skipped", ui)
                : ObservationUiPresentation.Text("OpenAstroSpec 整夜序列停止后续目标", "OpenAstroSpec night stopped further targets", ui),
            ObservationUiPresentation.Text(
                $"目标：{target}\n代码：{code}\n" + (skipped
                    ? "已确认采集、导星和设备运动停止，并保存跳过记录。整夜序列未因此终止，将按剩余时间和安全条件继续后续目标。"
                    : $"未满足跳过条件，后续目标已停止。请查看整夜状态与诊断；屋顶状态由独立收口策略决定。\n边界：{boundaryFailure ?? code}"),
                $"Target: {target}\nCode: {code}\n" + (skipped
                    ? "Acquisition, guiding and motion are confirmed stopped; the skip is saved. This did not terminate the night. Following targets remain subject to time and safety checks."
                    : $"Skip conditions were not met; further targets stopped. See night status and diagnostics. Roof state follows the separate closeout policy.\nBoundary: {boundaryFailure ?? code}"), ui),
            $"{runId}|NativeBoundary|{skipped}");
    }

    private static string ResolveCoordinatorCode(ObservationSnapshot snapshot)
    {
        var matching = snapshot.RecentEvents
            .LastOrDefault(item =>
                item.State == snapshot.State &&
                item.Stage == snapshot.CurrentStage &&
                item.Code is not "FAULT_CLEANUP_COMPLETED" and not "FAULT_CLEANUP_FAILED");
        return string.IsNullOrWhiteSpace(matching?.Code)
            ? snapshot.State == ObservationRunState.Faulted ? "RUN_FAULTED" : "NEEDS_ATTENTION"
            : matching.Code;
    }

    private static string FirstNonBlank(params string?[] candidates) =>
        candidates.First(candidate => !string.IsNullOrWhiteSpace(candidate))!.Trim();
}

internal interface IObservationAttentionNotifier : IDisposable
{
    void Notify(ObservationAttentionNotification notification);
    void ClearActiveIndicator();
}

/// <summary>
/// Emits both N.I.N.A.'s native in-app notification and a Windows shell
/// notification.  Every exception is contained here: notification delivery is
/// observability only and can never change the observation state machine.
/// </summary>
internal sealed class NinaAndWindowsObservationAttentionNotifier : IObservationAttentionNotifier
{
    private readonly object sync = new();
    private Forms.NotifyIcon? notifyIcon;
    private bool disposed;

    public void Notify(ObservationAttentionNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        DispatchBestEffort(() => ShowCore(notification));
    }

    public void ClearActiveIndicator() => DispatchBestEffort(() =>
    {
        lock (sync)
        {
            if (notifyIcon is not null) notifyIcon.Visible = false;
        }
    });

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (notifyIcon is not null)
            {
                notifyIcon.Visible = false;
                notifyIcon.Dispose();
                notifyIcon = null;
            }
        }
    }

    private void ShowCore(ObservationAttentionNotification notification)
    {
        try
        {
            var inAppMessage = $"{notification.Title}\n{notification.Body}";
            if (notification.Severity == ObservationAttentionSeverity.Success)
            {
                NINA.Core.Utility.Notification.Notification.ShowSuccess(inAppMessage);
            }
            else if (notification.Severity == ObservationAttentionSeverity.Error)
            {
                NINA.Core.Utility.Notification.Notification.ShowError(inAppMessage);
            }
            else
            {
                NINA.Core.Utility.Notification.Notification.ShowWarning(
                    inAppMessage,
                    TimeSpan.FromSeconds(30));
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"OpenAstroSpec could not show the N.I.N.A. attention notification: {ex}");
        }

        try
        {
            lock (sync)
            {
                if (disposed) return;
                notifyIcon ??= new Forms.NotifyIcon
                {
                    Text = "OpenAstroSpec Auto",
                };
                notifyIcon.Icon = notification.Severity switch
                {
                    ObservationAttentionSeverity.Success => System.Drawing.SystemIcons.Information,
                    ObservationAttentionSeverity.Error => System.Drawing.SystemIcons.Error,
                    _ => System.Drawing.SystemIcons.Warning,
                };
                notifyIcon.Visible = true;
                notifyIcon.ShowBalloonTip(
                    15_000,
                    Truncate(notification.Title, 63),
                    Truncate(notification.Body.Replace('\n', ' '), 255),
                    notification.Severity switch
                    {
                        ObservationAttentionSeverity.Success => Forms.ToolTipIcon.Info,
                        ObservationAttentionSeverity.Error => Forms.ToolTipIcon.Error,
                        _ => Forms.ToolTipIcon.Warning,
                    });
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"OpenAstroSpec could not show the Windows attention notification: {ex}");
        }
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";

    private static void DispatchBestEffort(Action action)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
                return;
            }
            _ = dispatcher.BeginInvoke(DispatcherPriority.Background, action);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"OpenAstroSpec could not dispatch an attention notification: {ex}");
        }
    }
}

#pragma warning restore CA1416
