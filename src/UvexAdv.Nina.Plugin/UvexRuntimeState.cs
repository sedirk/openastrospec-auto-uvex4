using System.Windows;
using System.Windows.Media;
using UvexAdv.Spectroscopy;

namespace UvexAdv.Nina.Plugin;

internal static class UvexRuntimeState
{
    public static event EventHandler? Changed;

    public static PointCollection SpectrumPoints { get; private set; } = CreateEmptySpectrumPoints();
    public static string MetricSummary { get; private set; } = "尚未采集光谱";

    public static void Publish(Spectrum1D spectrum, SdkWrapRepairResult? sdkWrap = null)
    {
        var maximum = Math.Max(1, spectrum.Flux.Max());
        var stride = Math.Max(1, spectrum.Flux.Length / 600);
        var points = new PointCollection();
        for (var index = 0; index < spectrum.Flux.Length; index += stride)
        {
            points.Add(new Point(index / (double)Math.Max(1, spectrum.Flux.Length - 1), 1 - (spectrum.Flux[index] / maximum)));
        }

        points.Freeze();
        SpectrumPoints = points;
        var wrapSummary = sdkWrap is null || !double.IsFinite(sdkWrap.SeamScoreSigma)
            ? string.Empty
            : sdkWrap.Applied
                ? $" · SDK 修复 {sdkWrap.AppliedShiftPixels:+#;-#;0} px ({sdkWrap.SeamScoreSigma:F1}σ)"
                : $" · SDK 接缝 {sdkWrap.SeamScoreSigma:F1}σ";
        MetricSummary = $"{spectrum.Flux.Length} px · 饱和 {spectrum.SaturatedFraction:P3}{wrapSummary}";
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static PointCollection CreateEmptySpectrumPoints()
    {
        var points = new PointCollection();
        points.Freeze();
        return points;
    }
}
