using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using UvexAdv.Spectroscopy;

namespace UvexAdv.Nina.Plugin;

internal sealed class NinaSpectrumCapture(
    ICameraMediator cameraMediator,
    IImagingMediator imagingMediator,
    UvexPluginSettings settings,
    IProgress<ApplicationStatus> progress)
{
    public async Task<Spectrum1D> CaptureAsync(CancellationToken cancellationToken)
    {
        ValidateCamera();
        var sequence = new CaptureSequence
        {
            ExposureTime = settings.ExposureSeconds,
            ImageType = CaptureSequence.ImageTypes.SNAPSHOT,
            Binning = new BinningMode(settings.Binning, settings.Binning),
            Gain = settings.Gain,
            Offset = settings.Offset,
            TotalExposureCount = 1,
            Dither = false,
        };
        var exposure = await imagingMediator.CaptureImage(sequence, cancellationToken, progress, "UVEX-ADV calibration").ConfigureAwait(false);
        var imageData = await exposure.ToImageData(progress, cancellationToken).ConfigureAwait(false);
        var properties = imageData.Properties;
        if (properties.IsBayered)
        {
            throw new InvalidOperationException("UVEX spectral closed loops require a monochrome image; the active N.I.N.A. image is Bayered.");
        }

        var source = imageData.Data.FlatArray;
        if (source.Length != properties.Width * properties.Height)
        {
            throw new InvalidOperationException("N.I.N.A. returned an unsupported image array representation.");
        }

        var pixels = Array.ConvertAll(source, static value => (double)value);
        var sdkWrap = settings.AutoRepairAtr585mSdkWrap && settings.DispersionAxis == DispersionAxis.Horizontal
            ? Atr585mSdkWrapRepair.DetectAndRepairHorizontal(
                pixels,
                properties.Width,
                properties.Height,
                settings.Roi,
                settings.ApertureStart,
                settings.ApertureLength,
                settings.SdkWrapShiftPixels,
                settings.SdkWrapSeamSigma)
            : new SdkWrapRepairResult(double.NaN, false, 0, "Automatic repair is disabled or the dispersion axis is not horizontal.");
        var saturation = Math.Pow(2, Math.Clamp(properties.BitDepth, 1, 16)) - 1;
        var image = new SpectralImage(properties.Width, properties.Height, pixels, saturation);
        var spectrum = SpectrumExtractor.Extract(image, new SpectrumExtractionOptions(
            settings.Roi,
            settings.DispersionAxis,
            settings.ApertureStart,
            settings.ApertureLength));
        UvexRuntimeState.Publish(spectrum, sdkWrap);
        try
        {
            await LoopRunLogger.WriteAsync("spectrum-capture", new
            {
                width = properties.Width,
                height = properties.Height,
                spectrum.SaturatedFraction,
                sdkWrap,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Capture and safety behavior must not depend on diagnostic-log availability.
        }

        return spectrum;
    }

    private void ValidateCamera()
    {
        var info = cameraMediator.GetInfo();
        if (!info.Connected)
        {
            throw new InvalidOperationException("No camera is connected in N.I.N.A.");
        }

        var identity = string.Join('|', info.Name, info.DisplayName, info.Description, info.DeviceId);
        if (!AtrCameraIdentityGate.Matches(
                identity,
                info.DeviceId,
                settings.ExpectedCameraName,
                settings.BoundCameraId))
        {
            var requirement = string.IsNullOrWhiteSpace(settings.BoundCameraId)
                ? $"the fallback model name '{settings.ExpectedCameraName}'"
                : $"the bound DeviceId '{settings.BoundCameraId}'";
            throw new InvalidOperationException(
                $"The active N.I.N.A. camera '{info.DisplayName ?? info.Name}' does not match {requirement}. Bind the ATR585M stable DeviceId from the OpenAstroSpec Spectrum panel before closed-loop operation.");
        }
    }
}

internal static class AtrCameraIdentityGate
{
    internal static bool Matches(
        string actualIdentity,
        string? actualDeviceId,
        string expectedModelName,
        string? boundDeviceId)
    {
        if (!string.IsNullOrWhiteSpace(boundDeviceId))
        {
            return string.Equals(actualDeviceId, boundDeviceId, StringComparison.Ordinal);
        }

        return !string.IsNullOrWhiteSpace(expectedModelName) &&
            actualIdentity.Contains(expectedModelName, StringComparison.OrdinalIgnoreCase);
    }
}
