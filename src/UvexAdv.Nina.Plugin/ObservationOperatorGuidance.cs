using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Converts machine-oriented quality-gate results into stable operator guidance.
/// It contains no device access and is deliberately testable without N.I.N.A.
/// </summary>
public static class ObservationOperatorGuidance
{
    public static ObservationFailureGuidance For(ObservationStage stage, GateResult gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var code = gate.Code.ToUpperInvariant();

        if (code.Contains("FOCUS", StringComparison.Ordinal) ||
            code.Contains("STELLAR", StringComparison.Ordinal))
        {
            return new ObservationFailureGuidance(
                ObservationPreviewChannel.G3SlitField,
                "PHD2 / G3 主镜星点图",
                "放大 G3 图检查真实星点数量、FWHM、饱和和云层。这里的星点属于 C11 主焦面；只能用 Star Focuser Pro / Gemini 主调焦器修正，不能用 UVEX M2 或 GS350 的 ToupTek AAF 代偿。");
        }

        if (code.StartsWith("SLIT_PENDING", StringComparison.Ordinal) ||
            code.StartsWith("SLIT_BUDGET", StringComparison.Ordinal) ||
            code.StartsWith("SLIT_RETURN", StringComparison.Ordinal) ||
            code.Contains("SEGMENT_RETURN", StringComparison.Ordinal))
        {
            return new ObservationFailureGuidance(
                ObservationPreviewChannel.G3SlitField,
                "入缝分段恢复（实报位置）",
                "本轮入缝分段的恢复记录仍在磁盘上，自动流程不会遗忘或跨越它。先核对错误中的实报位置、命令残差、单次/累计/次数预算和地平线门；可按“恢复”让程序先回到本段起点，或明确进入人工接管。不要删除 pending 文件来绕过恢复。");
        }

        if (code.Contains("SLIT", StringComparison.Ordinal) ||
            code.Contains("GUIDE", StringComparison.Ordinal) ||
            code.Contains("PHD", StringComparison.Ordinal) ||
            code.StartsWith("G3_", StringComparison.Ordinal))
        {
            return new ObservationFailureGuidance(
                ObservationPreviewChannel.G3SlitField,
                "PHD2 / G3 狭缝与导星图",
                "放大 G3 图检查目标质心、LED 差分狭缝线、导星星和残差叠加；再打开失败证据核对本次 FITS、WCS、狭缝几何或 PHD2 事件。不要用旧画面代替本次证据。");
        }

        if (code.Contains("QHY", StringComparison.Ordinal) ||
            code.Contains("PLATE", StringComparison.Ordinal) ||
            code.Contains("SOLVE", StringComparison.Ordinal) ||
            code.Contains("WCS", StringComparison.Ordinal) ||
            code.Contains("COARSE", StringComparison.Ordinal))
        {
            return new ObservationFailureGuidance(
                ObservationPreviewChannel.QhyWideField,
                "GS350 / QHY 广域解算图",
                "放大 QHY 图检查 R 滤镜读回、真实星数、星形、饱和、云层和 WCS 标注；失败时保留原始 FITS 与 solver sidecar，再决定是否重拍或跳过可选的两镜预定位。");
        }

        if (code.Contains("ATR", StringComparison.Ordinal) ||
            code.Contains("SPECTR", StringComparison.Ordinal) ||
            code.Contains("SCIENCE", StringComparison.Ordinal) ||
            code.Contains("EXPOSURE", StringComparison.Ordinal) ||
            stage is ObservationStage.SelectAtrExposure or ObservationStage.RunScienceBlock)
        {
            return new ObservationFailureGuidance(
                ObservationPreviewChannel.AtrSpectrum,
                "N.I.N.A. / ATR585M 二维与一维光谱",
                "放大 ATR 图检查二维 ROI、饱和像素、谱线位置/FWHM、即时一维曲线、SNR 与制冷遥测；原始 FITS 应继续保留，未通过质量门的帧不会计入合格科学帧。");
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
                "真实模式启动条件与运行清单",
                "先查看真实模式启动条件和运行时间线。核对 Night Setup、commissioning 哈希、设备身份、屋顶/天气、地平线、赤道仪时钟和运行清单；这些联锁失败通常没有可解释的天文图像。");
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
            channel is null ? "质量门与运行时间线" : ChannelDisplayName(channel.Value),
            "先阅读完整错误代码和质量门数值，再打开最近证据与运行目录。恢复不会绕过门，而会重新核验可能过期的设备、安全和质量状态。");
    }

    public static string ChannelDisplayName(ObservationPreviewChannel channel) => channel switch
    {
        ObservationPreviewChannel.QhyWideField => "GS350 / QHY 广域解算图",
        ObservationPreviewChannel.G3SlitField => "PHD2 / G3 狭缝与导星图",
        ObservationPreviewChannel.AtrSpectrum => "N.I.N.A. / ATR585M 光谱图",
        _ => channel.ToString(),
    };
}

public sealed record ObservationFailureGuidance(
    ObservationPreviewChannel? PreviewChannel,
    string PreviewLabel,
    string Recommendation);
