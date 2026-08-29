using System.Globalization;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Converts machine-oriented quality-gate results into stable operator guidance.
/// It contains no device access and is deliberately testable without N.I.N.A.
/// </summary>
public static class ObservationOperatorGuidance
{
    public static ObservationFailureGuidance For(ObservationStage stage, GateResult gate) =>
        For(stage, gate, CultureInfo.CurrentUICulture);

    public static ObservationFailureGuidance For(
        ObservationStage stage,
        GateResult gate,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var code = gate.Code.ToUpperInvariant();

        if (code.StartsWith("GS350_FOCUS", StringComparison.Ordinal) ||
            (stage == ObservationStage.AcquireQhyWideField &&
             code.Contains("FOCUS", StringComparison.Ordinal)))
        {
            return new ObservationFailureGuidance(
                ObservationPreviewChannel.QhyWideField,
                T("GS350 / QHY 广域星形图", "GS350 / QHY wide-field star profile", culture),
                T("放大 QHY 图检查当前 R 滤镜、曝光、真实星数、FWHM、饱和与云层。这些星点属于 GS350 广域光路；若曝光阶梯用尽后仍无法测量星形，只能检查或手动调整 GS350 的 ToupTek AAF，不要用 C11/Star Focuser Pro 或 UVEX M2 代偿。",
                  "Inspect the QHY frame at full scale for the R filter, exposure, real-star count, FWHM, saturation and cloud. These stars belong to the GS350 wide-field path; if the exposure ladder is exhausted, inspect or manually adjust only the GS350 ToupTek AAF, not the C11 focuser or UVEX M2.", culture));
        }

        if (code.Contains("FOCUS", StringComparison.Ordinal) ||
            code.Contains("STELLAR", StringComparison.Ordinal))
        {
            return new ObservationFailureGuidance(
                ObservationPreviewChannel.G3SlitField,
                T("PHD2 / G3 主镜星点图", "PHD2 / G3 main-focus star profile", culture),
                T("放大 G3 图检查真实星点数量、FWHM、饱和和云层。这里的星点属于 C11 主焦面；只能用 Star Focuser Pro / Gemini 主调焦器修正，不能用 UVEX M2 或 GS350 的 ToupTek AAF 代偿。",
                  "Inspect the G3 frame at full scale for real-star count, FWHM, saturation and cloud. These stars belong to the C11 main focal plane; adjust only the Star Focuser Pro/Gemini main focuser, not UVEX M2 or the GS350 ToupTek AAF.", culture));
        }

        if (code.StartsWith("SLIT_PENDING", StringComparison.Ordinal) ||
            code.StartsWith("SLIT_BUDGET", StringComparison.Ordinal) ||
            code.StartsWith("SLIT_RETURN", StringComparison.Ordinal) ||
            code.Contains("SEGMENT_RETURN", StringComparison.Ordinal))
        {
            return new ObservationFailureGuidance(
                ObservationPreviewChannel.G3SlitField,
                T("入缝分段恢复（实报位置）", "Segmented slit-placement recovery (reported position)", culture),
                T("本轮入缝分段的恢复记录仍在磁盘上，自动流程不会遗忘或跨越它。先核对错误中的实报位置、命令残差、单次/累计/次数预算和地平线门；可按“恢复”让程序先回到本段起点，或明确进入人工接管。不要删除 pending 文件来绕过恢复。",
                  "The durable segmented-placement record remains on disk and cannot be skipped. Check the reported position, command residual, per-step/cumulative/attempt budgets and horizon gate; Resume may first return to the segment origin, or enter manual takeover. Do not delete the pending file to bypass recovery.", culture));
        }

        if (code.Contains("SLIT", StringComparison.Ordinal) ||
            code.Contains("GUIDE", StringComparison.Ordinal) ||
            code.Contains("PHD", StringComparison.Ordinal) ||
            code.StartsWith("G3_", StringComparison.Ordinal))
        {
            return new ObservationFailureGuidance(
                ObservationPreviewChannel.G3SlitField,
                T("PHD2 / G3 狭缝与导星图", "PHD2 / G3 slit and guiding frame", culture),
                T("放大 G3 图检查目标质心、LED 差分狭缝线、导星星和残差叠加；再打开失败证据核对本次 FITS、WCS、狭缝几何或 PHD2 事件。不要用旧画面代替本次证据。",
                  "Inspect the G3 frame at full scale for the target centroid, LED-difference slit, guide star and residual overlays; then inspect this run's FITS, WCS, slit geometry or PHD2 event. Do not substitute an old preview for current evidence.", culture));
        }

        if (code.Contains("QHY", StringComparison.Ordinal) ||
            code.Contains("PLATE", StringComparison.Ordinal) ||
            code.Contains("SOLVE", StringComparison.Ordinal) ||
            code.Contains("WCS", StringComparison.Ordinal) ||
            code.Contains("COARSE", StringComparison.Ordinal))
        {
            return new ObservationFailureGuidance(
                ObservationPreviewChannel.QhyWideField,
                T("GS350 / QHY 广域解算图", "GS350 / QHY wide-field solve", culture),
                T("放大 QHY 图检查 R 滤镜读回、真实星数、星形、饱和、云层和 WCS 标注；失败时保留原始 FITS 与 solver sidecar，再决定是否重拍或跳过可选的两镜预定位。",
                  "Inspect the QHY frame for R-filter readback, real-star count, star shapes, saturation, cloud and WCS overlay. Preserve the raw FITS and solver sidecar before deciding whether to reacquire or skip optional dual-scope pre-positioning.", culture));
        }

        if (code.Contains("ATR", StringComparison.Ordinal) ||
            code.Contains("SPECTR", StringComparison.Ordinal) ||
            code.Contains("SCIENCE", StringComparison.Ordinal) ||
            code.Contains("EXPOSURE", StringComparison.Ordinal) ||
            stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock)
        {
            return new ObservationFailureGuidance(
                ObservationPreviewChannel.AtrSpectrum,
                T("N.I.N.A. / ATR585M 二维与一维光谱", "N.I.N.A. / ATR585M 2D and 1D spectrum", culture),
                T("放大 ATR 图检查二维 ROI、饱和像素、谱线位置/FWHM、即时一维曲线、SNR 与制冷遥测；原始 FITS 应继续保留，未通过质量门的帧不会计入合格科学帧。",
                  "Inspect the ATR frame at full scale for the 2D ROI, saturated pixels, line position/FWHM, live 1D trace, SNR and cooling telemetry. Preserve the raw FITS; a frame that fails its gate is not counted as accepted science data.", culture));
        }

        if (stage == ObservationStage.ValidateNightSetup ||
            code.Contains("IDENTITY", StringComparison.Ordinal) ||
            code.Contains("WEATHER", StringComparison.Ordinal) ||
            code.Contains("HORIZON", StringComparison.Ordinal) ||
            code.Contains("ROOF", StringComparison.Ordinal) ||
            code.Contains("CLOCK", StringComparison.Ordinal) ||
            code.Contains("MANIFEST", StringComparison.Ordinal))
        {
            return new ObservationFailureGuidance(
                null,
                T("真实模式启动条件与运行清单", "Real-mode startup conditions and run manifest", culture),
                T("先查看真实模式启动条件和运行时间线。核对 Night Setup、commissioning 哈希、设备身份、屋顶/天气、地平线、赤道仪时钟和运行清单；这些联锁失败通常没有可解释的天文图像。",
                  "Inspect real-mode startup conditions and the run timeline. Check Night Setup, commissioning hashes, device identity, roof/weather, horizon, mount clock and manifest; these interlock failures normally have no useful astronomical image.", culture));
        }

        var channel = stage switch
        {
            ObservationStage.AcquireQhyWideField or ObservationStage.CoarseCenter or ObservationStage.StartQhyPhotometry => ObservationPreviewChannel.QhyWideField,
            ObservationStage.AcquireG3SlitField or ObservationStage.PlaceTargetOnSlit or ObservationStage.StartGuiding => ObservationPreviewChannel.G3SlitField,
            ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock => ObservationPreviewChannel.AtrSpectrum,
            _ => (ObservationPreviewChannel?)null,
        };
        return new ObservationFailureGuidance(
            channel,
            channel is null ? T("质量门与运行时间线", "Quality gates and run timeline", culture) : ChannelDisplayName(channel.Value, culture),
            T("先阅读完整错误代码和质量门数值，再打开最近证据与运行目录。恢复不会绕过门，而会重新核验可能过期的设备、安全和质量状态。",
              "Read the complete code and gate metrics, then open the latest evidence and run directory. Resume never bypasses a gate; it revalidates device, safety and quality state that may be stale.", culture));
    }

    public static string ChannelDisplayName(ObservationPreviewChannel channel) =>
        ChannelDisplayName(channel, CultureInfo.CurrentUICulture);

    public static string ChannelDisplayName(ObservationPreviewChannel channel, CultureInfo culture) => channel switch
    {
        ObservationPreviewChannel.QhyWideField => T("GS350 / QHY 广域解算图", "GS350 / QHY wide-field solve", culture),
        ObservationPreviewChannel.G3SlitField => T("PHD2 / G3 狭缝与导星图", "PHD2 / G3 slit and guiding frame", culture),
        ObservationPreviewChannel.AtrSpectrum => T("N.I.N.A. / ATR585M 光谱图", "N.I.N.A. / ATR585M spectrum", culture),
        _ => channel.ToString(),
    };

    private static string T(string chinese, string english, CultureInfo culture) =>
        ObservationUiPresentation.Text(chinese, english, culture);
}

public sealed record ObservationFailureGuidance(
    ObservationPreviewChannel? PreviewChannel,
    string PreviewLabel,
    string Recommendation);
