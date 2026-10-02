using System.IO;
using System.Windows.Media.Imaging;
using UvexAdv.Spectroscopy;

namespace UvexAdv.Nina.Plugin;

/// <summary>Optional display-only refinement. One worker, no queue, no acquisition waits.</summary>
internal static class AtrReductionPreview
{
    private static readonly SemaphoreSlim Slot = new(1, 1);

    internal static async Task RefineAsync(ObservationCoordinatorHost host, BitmapSource initial,
        ushort[] pixels, int width, int height, double saturation, double center, double halfWidth, string caption)
    {
        if (!await Slot.WaitAsync(0).ConfigureAwait(false))
        {
            try
            {
                host.TryReplacePreview(ObservationPreviewChannel.AtrSpectrum, initial, initial,
                    caption + " · 后期引擎忙：本帧保留未清理的背景扣除快览。");
            }
            catch { /* A display observer cannot fault the fire-and-forget preview task. */ }
            return;
        }
        try
        {
            // All work (including Python discovery/start) is on the pool, not
            // the capture path or WPF dispatcher. Own only this copied buffer.
            var result = await Task.Run(async () =>
            {
                var python = ReductionPreviewClient.FindPython(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
                var worker = Path.Combine(Path.GetDirectoryName(typeof(AtrReductionPreview).Assembly.Location)!, "ReductionPreview", "live_preview_worker.py");
                return await ReductionPreviewClient.ExtractAsync(python, worker, pixels, width, height,
                    saturation, center, halfWidth, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
            var updated = ObservationPreviewRenderer.WithReducedSpectrum(initial, result);
            host.TryReplacePreview(ObservationPreviewChannel.AtrSpectrum, initial, updated,
                caption + $" · 后期预览 {result.Backend}；清理候选 {result.CosmicPixels} 像素，缺测 {result.MaskedColumns} 列；未做暗场/平场/波长/响应标定。" +
                (result.Warnings.Length > 0 ? " · 提取有回退/警告：" + string.Join("；", result.Warnings) : string.Empty));
        }
        catch (Exception ex)
        {
            var reason = ex is OperationCanceledException ? "超过 45 秒" : ex.Message;
            try
            {
                host.TryReplacePreview(ObservationPreviewChannel.AtrSpectrum, initial, initial,
                    caption + " · 后期预览不可用，保留未清理的背景扣除快览：" + reason);
            }
            catch { /* Preview observers cannot alter acquisition or leave an unobserved task. */ }
        }
        finally { Slot.Release(); }
    }
}
