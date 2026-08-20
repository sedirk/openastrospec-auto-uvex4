using UvexAdv.Core;

namespace UvexAdv.Admin;

internal static class SlitIlluminationPresentation
{
    private const string Label = "定位LED（非Calibrex）";
    private const string EvidenceScope = "仅为 UVEX 定位LED/光电诊断，不是 G3 图像中的光学狭缝判定。";

    public static string FormatCommandState(UvexDeviceStatus? status)
    {
        if (status is null)
        {
            return $"{Label}：状态未知（服务未连接）";
        }

        var state = status.SlitIlluminationLedState switch
        {
            UvexOutputState.On => "服务最近成功命令：开",
            UvexOutputState.Off => "服务最近成功命令：关",
            _ => "状态未知（协议不能回读实际开关态）",
        };
        var commanded = status.SlitIlluminationLedCommandedUtc is { } timestamp
            ? $" · 命令时间：{timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : string.Empty;
        return $"{Label}：{state}{commanded}";
    }

    public static string FormatPhotodiode(UvexDeviceStatus? status)
    {
        var value = status?.SlitPhotodiodeValue;
        var threshold = status?.SlitPhotodiodeThreshold;
        var comparison = value.HasValue && threshold.HasValue
            ? value.Value > threshold.Value ? "超过阈值" : "未超过阈值"
            : "无法核对响应";
        return $"UVEX 光电值：{value?.ToString() ?? "?"} · 阈值：{threshold?.ToString() ?? "?"} · {comparison}";
    }

    public static string FormatWarning(UvexDeviceStatus? status)
    {
        if (status?.SlitIlluminationLedState != UvexOutputState.On)
        {
            return string.Empty;
        }

        if (status.SlitPhotodiodeValue is { } value && status.SlitPhotodiodeThreshold is { } threshold)
        {
            return value > threshold
                ? string.Empty
                : $"警告：开灯后光电值 {value} 未超过阈值 {threshold}。{EvidenceScope}";
        }

        return $"警告：开灯后缺少光电值或阈值，无法核对响应。{EvidenceScope}";
    }
}
