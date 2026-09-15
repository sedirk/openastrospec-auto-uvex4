using System.Globalization;
using NINA.Equipment.Interfaces;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private async Task ConfirmAtrTemperatureMetadataBeforeFirstSaveAsync(IImageData image,
        string expectedCameraId, string captureId, CancellationToken token)
    {
        // GetDevice returns the instance already owned by N.I.N.A.; never create,
        // connect or load a SDK. GetInfo cannot provide a fresh hardware sample.
        var owner = cameraMediator.GetDevice() as ICamera;
        if (owner is null || !AtrTemperatureMetadataConfirmation.NeedsConfirmation(owner.GetType().FullName,
            image.MetaData.Camera.Temperature, image.MetaData.Camera.SetPoint, configuration.Atr.TargetTemperatureC)) return;
        Report("本帧温度元数据为 0°C，与目标温度不一致；首次保存前只读复核同一相机实温，不重连、不补拍。");
        var result = await AtrTemperatureMetadataConfirmation.ConfirmAsync(owner.GetType().FullName,
            image.MetaData.Camera.Temperature, image.MetaData.Camera.SetPoint, configuration.Atr.TargetTemperatureC,
            expectedCameraId, ct => ReadFreshAtrOwnerAsync(owner, expectedCameraId, image.MetaData.Camera.Id, ct),
            ct => Task.Delay(TimeSpan.FromSeconds(1), ct), token).ConfigureAwait(false);

        // Retain the native value and measured source even in the first FITS.
        // Existing files are never reopened for writing or repaired retroactively.
        AtrTemperatureMetadataHeaders.ApplyBeforeFirstSave(image.MetaData, result);
        await WriteAuditBestEffortAsync("atr-temperature-metadata-confirmation", new { captureId, result,
            beforeFirstSave = true, cameraReconnected = false, exposureRepeated = false }).ConfigureAwait(false);
    }

    private Task<AtrCoolingTelemetry> ReadFreshAtrOwnerAsync(ICamera owner, string expectedCameraId,
        string captureCameraId, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var info = cameraMediator.GetInfo();
        if (!ReferenceEquals(owner, cameraMediator.GetDevice()) || !info.Connected ||
            info.DeviceId != expectedCameraId || captureCameraId != expectedCameraId)
            throw new InvalidOperationException("The capture's N.I.N.A. camera owner changed.");
        var sample = new AtrCoolingTelemetry(owner.Connected, owner.Id, owner.CanSetTemperature,
            owner.CoolerOn, owner.Temperature, owner.TemperatureSetPoint, owner.CoolerPower);
        if (!ReferenceEquals(owner, cameraMediator.GetDevice()) || !owner.Connected)
            throw new InvalidOperationException("The N.I.N.A. camera owner disconnected during readback.");
        return sample;
    }, token);

    private async Task<GateResult> ReadAtrPostSaveTemperatureAsync(AtrCapture capture, CancellationToken token)
    {
        var cached = ReadAtrCoolingReadiness(capture.ExpectedCameraId);
        if (cached.Code != "ATR_PRECOOLING_IN_PROGRESS" || cached.Metrics?.GetValueOrDefault("temperatureC") != 0) return cached;
        var owner = cameraMediator.GetDevice() as ICamera;
        if (owner is null) return cached;
        var result = await AtrTemperatureMetadataConfirmation.ConfirmAsync(owner.GetType().FullName, 0,
            capture.Image.MetaData.Camera.SetPoint, configuration.Atr.TargetTemperatureC, capture.ExpectedCameraId,
            ct => ReadFreshAtrOwnerAsync(owner, capture.ExpectedCameraId, capture.Image.MetaData.Camera.Id, ct),
            ct => Task.Delay(TimeSpan.FromSeconds(1), ct), token).ConfigureAwait(false);
        await WriteAuditBestEffortAsync("atr-post-save-temperature-readback", new { capture.CaptureToken, result,
            originalFitsUnchanged = true }).ConfigureAwait(false);
        return result.Applied ? AtrCoolingReadinessPolicy.Evaluate(result.Samples[^1], capture.ExpectedCameraId,
            configuration.Atr.TargetTemperatureC) : cached;
    }
}

internal static class AtrTemperatureMetadataHeaders
{
    internal static void ApplyBeforeFirstSave(ImageMetaData metadata, AtrTemperatureConfirmation result)
    {
        if (result.Code == "NATIVE_METADATA_UNCHANGED") return;
        metadata.GenericHeaders.Add(new StringMetaDataHeader("UVEXTN", result.OriginalTemperatureC.ToString("R", CultureInfo.InvariantCulture), "Original native download temperature C"));
        metadata.GenericHeaders.Add(new StringMetaDataHeader("UVEXTSRC", result.Code, "Temperature metadata origin / readback outcome"));
        if (result.Applied)
        {
            metadata.Camera.Temperature = result.ConfirmedTemperatureC!.Value;
            metadata.GenericHeaders.Add(new StringMetaDataHeader("UVEXTUTC", result.ConfirmedUtc!.Value.ToString("O", CultureInfo.InvariantCulture), "Fresh end-of-capture temperature confirmation UTC"));
        }
        for (var i = 0; i < result.Samples.Count; i++)
        {
            var sample = result.Samples[i];
            metadata.GenericHeaders.Add(new StringMetaDataHeader($"UVEXT{i + 1}", sample.TemperatureC.ToString("R", CultureInfo.InvariantCulture), "Fresh exact-owner temperature sample C"));
        }
    }
}
