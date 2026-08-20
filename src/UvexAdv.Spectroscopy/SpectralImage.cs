namespace UvexAdv.Spectroscopy;

public enum DispersionAxis
{
    Horizontal,
    Vertical,
}

public readonly record struct ImageRoi(int X, int Y, int Width, int Height)
{
    public void Validate(int imageWidth, int imageHeight)
    {
        if (X < 0 || Y < 0 || Width < 3 || Height < 3 || X + Width > imageWidth || Y + Height > imageHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(ImageRoi), "ROI is outside the image or too small.");
        }
    }
}

public sealed class SpectralImage
{
    private readonly ReadOnlyMemory<double> pixels;

    public SpectralImage(int width, int height, ReadOnlyMemory<double> pixels, double saturationLevel)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height))
        {
            throw new ArgumentException("Image dimensions do not match the pixel buffer.");
        }

        Width = width;
        Height = height;
        this.pixels = pixels;
        SaturationLevel = saturationLevel;
    }

    public int Width { get; }
    public int Height { get; }
    public double SaturationLevel { get; }
    public double this[int x, int y] => pixels.Span[(y * Width) + x];
}

public sealed record Spectrum1D(double[] Flux, double SaturatedFraction, ImageRoi SourceRoi, DispersionAxis Axis);
