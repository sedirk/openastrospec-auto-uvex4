using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal static class G3MosaicImage
{
    internal static IImageData Create(IImageData current, IImageDataFactory factory,
        G3MosaicComposition composition)
    {
        if (current.Properties.IsBayered) throw new ArgumentException("G3 derived mosaic requires monochrome sources.");
        // Deliberately do not mutate/copy generic WCS cards from an input frame.
        var metadata = new ImageMetaData
        {
            Image = new ImageParameter { ImageType = "ACQUISITION", ExposureTime = current.MetaData.Image.ExposureTime },
            Camera = new CameraParameter { Id = current.MetaData.Camera.Id, Name = current.MetaData.Camera.Name,
                BinX = current.MetaData.Camera.BinX, BinY = current.MetaData.Camera.BinY,
                PixelSize = current.MetaData.Camera.PixelSize },
            Target = new TargetParameter { Name = current.MetaData.Target.Name },
            Telescope = new TelescopeParameter { Name = current.MetaData.Telescope.Name,
                FocalLength = current.MetaData.Telescope.FocalLength },
        };
        metadata.GenericHeaders.Add(new BoolMetaDataHeader("DERIVED", true, "Registered pixel mosaic; not a raw observation"));
        metadata.GenericHeaders.Add(new IntMetaDataHeader("NPARENTS", composition.SourceSha256.Count, "Immutable registered source frames"));
        return factory.CreateBaseImageData(new ImageArray((ushort[])composition.Pixels.Clone()),
            composition.Width, composition.Height, 16, false, metadata);
    }
}
