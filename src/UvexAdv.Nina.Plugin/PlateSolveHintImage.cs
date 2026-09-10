using NINA.Astrometry;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace UvexAdv.Nina.Plugin;

/// <summary>A disposable solver input, never an observation or a WCS witness.</summary>
internal static class PlateSolveHintImage
{
    internal static IImageData Create(
        IImageData source,
        IImageDataFactory factory,
        Coordinates requested,
        double focalLengthMillimeters,
        double pixelSizeMicrometers,
        int binning,
        int softwareFactor = 1)
    {
        var metadata = CreateMetadata(source.MetaData, requested,
            focalLengthMillimeters, pixelSizeMicrometers * softwareFactor, binning);
        if (softwareFactor > 1)
            metadata.GenericHeaders.Add(new IntMetaDataHeader("SWBIN", softwareFactor,
                "Disposable solver input software block mean; not camera binning"));
        // Neither a solver's metadata edits nor image preprocessing may alter the source.
        return factory.CreateBaseImageData(PlateSolveSoftwareBinning.CopyBlockMeans(source.Data,
                source.Properties.Width, source.Properties.Height, softwareFactor),
            source.Properties.Width / softwareFactor, source.Properties.Height / softwareFactor, source.Properties.BitDepth,
            source.Properties.IsBayered, metadata);
    }

    internal static IImageArray CopyPixels(IImageArray source) =>
        source.FlatArrayInt is { } wide
            ? new ImageArrayInt((int[])wide.Clone())
            : new ImageArray((ushort[])source.FlatArray.Clone());

    internal static ImageMetaData CreateMetadata(
        ImageMetaData source,
        Coordinates requested,
        double focalLengthMillimeters,
        double pixelSizeMicrometers,
        int binning)
    {
        if (!double.IsFinite(requested.RADegrees) || !double.IsFinite(requested.Dec) ||
            requested.Dec is < -90 or > 90)
            throw new ArgumentException("The plate-solve hint must be a finite sky coordinate.", nameof(requested));
        var hint = new Coordinates(requested.RADegrees, requested.Dec, requested.Epoch,
            Coordinates.RAType.Degrees).Transform(Epoch.J2000);

        // PlateSolve3 prefers FITS header coordinates over CLI coordinates. NINA's CLI
        // adapter also fills an absent target coordinate from the telescope metadata.
        // Populate BOTH in this isolated input; do not carry old WCS or generic RA/DEC
        // cards into it. This is a search hint, not the measured mount or target position.
        var metadata = new ImageMetaData
        {
            Image = new ImageParameter
            {
                ExposureStart = source.Image.ExposureStart,
                ExposureMidPoint = source.Image.ExposureMidPoint,
                ExposureNumber = source.Image.ExposureNumber,
                ExposureTime = source.Image.ExposureTime,
                ImageType = source.Image.ImageType,
                Binning = $"{binning}x{binning}",
            },
            Camera = new CameraParameter
            {
                Id = source.Camera.Id,
                Name = source.Camera.Name,
                BinX = binning,
                BinY = binning,
                PixelSize = pixelSizeMicrometers,
                Gain = source.Camera.Gain,
                Offset = source.Camera.Offset,
                BayerPattern = source.Camera.BayerPattern,
            },
            Target = new TargetParameter
            {
                Name = source.Target.Name,
                Coordinates = new Coordinates(hint.RADegrees, hint.Dec, Epoch.J2000, Coordinates.RAType.Degrees),
            },
            Telescope = new TelescopeParameter
            {
                Name = source.Telescope.Name,
                FocalLength = focalLengthMillimeters,
                Coordinates = new Coordinates(hint.RADegrees, hint.Dec, Epoch.J2000, Coordinates.RAType.Degrees),
            },
        };
        metadata.GenericHeaders.Add(new BoolMetaDataHeader("HINTONLY", true,
            "Solver input only; coordinates are search hints, not WCS"));
        return metadata;
    }
}
