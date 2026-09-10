using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving;

namespace UvexAdv.Nina.Plugin;

/// <summary>Resamples only a disposable solver input, never a camera or observation.</summary>
internal static class PlateSolveSoftwareBinning
{
    // NINA 3.2's PL3 CLI adapter does not use DownSampleFactor. Other solvers
    // retain their own implementation. Whole blocks preserve the full field;
    // never crop, pad, or average an undebayered colour mosaic here.
    internal static int SelectFactor(string role, string solverIdentity, int requested,
        int width, int height, bool isBayered) =>
        role.StartsWith("PHD2/G3", StringComparison.OrdinalIgnoreCase) &&
        solverIdentity.StartsWith("NINA.PlateSolving.Solvers.Platesolve3Solver,", StringComparison.Ordinal) &&
        requested is >= 2 and <= 8 && !isBayered &&
        width % requested == 0 && height % requested == 0 &&
        width / requested >= 32 && height / requested >= 32
            ? requested : 1;

    internal static IImageArray CopyBlockMeans(IImageArray source, int width, int height, int factor)
    {
        if (width <= 0 || height <= 0 || factor is < 1 or > 8 ||
            width % factor != 0 || height % factor != 0)
            throw new ArgumentException("Software binning requires complete, uncropped image blocks.");
        var wide = source.FlatArrayInt;
        var narrow = wide is null ? source.FlatArray : null;
        if ((wide?.Length ?? narrow!.Length) != checked(width * height))
            throw new ArgumentException("Pixel count does not match the source detector geometry.");
        if (factor == 1) return PlateSolveHintImage.CopyPixels(source);
        var outputWidth = width / factor;
        var count = checked(outputWidth * (height / factor));
        var outputWide = wide is null ? null : new int[count];
        var outputNarrow = wide is null ? new ushort[count] : null;
        for (var y = 0; y < height / factor; y++)
        for (var x = 0; x < outputWidth; x++)
        {
            long sum = 0;
            for (var j = 0; j < factor; j++)
            for (var i = 0; i < factor; i++)
            {
                var index = (y * factor + j) * width + x * factor + i;
                sum += wide is null ? narrow![index] : wide[index];
            }
            var mean = (int)Math.Round((double)sum / (factor * factor), MidpointRounding.ToEven);
            var outputIndex = y * outputWidth + x;
            if (outputWide is not null) outputWide[outputIndex] = mean;
            else outputNarrow![outputIndex] = (ushort)mean;
        }
        return outputWide is not null ? new ImageArrayInt(outputWide) : new ImageArray(outputNarrow!);
    }

    internal static PlateSolveResult RestoreDetectorScale(PlateSolveResult result, int factor)
    {
        if (factor is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(factor));
        // Full-field block means keep the sky centre, angle and angular radius.
        // Only arcseconds/pixel changes; consumers still use the ORIGINAL dimensions.
        return new PlateSolveResult(result.SolveTime)
        {
            Success = result.Success,
            Coordinates = result.Coordinates,
            Pixscale = result.Pixscale / factor,
            PositionAngle = result.PositionAngle,
            Radius = result.Radius,
            Flipped = result.Flipped,
            Separation = result.Separation,
        };
    }
}
