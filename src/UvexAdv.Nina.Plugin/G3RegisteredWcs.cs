using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

/// <summary>Re-express an astrometric anchor on an independently registered fresh detector.
/// The returned representation is inferred; callers must NOT relabel it a formal current-frame solve.</summary>
internal static class G3RegisteredWcs
{
    internal static PlateSolveResult RestoreCurrentResult(PlateSolveResult anchor, int anchorWidth, int anchorHeight,
        string solverIdentity, G3SimilarityTransform anchorToCurrent, int currentWidth, int currentHeight)
    {
        if (!anchor.Success || anchor.Coordinates is null || !double.IsFinite(anchor.Pixscale) || anchor.Pixscale <= 0 ||
            currentWidth <= 0 || currentHeight <= 0 || anchorWidth <= 0 || anchorHeight <= 0 ||
            !double.IsFinite(anchorToCurrent.Scale) || anchorToCurrent.Scale is < .9 or > 1.1)
            throw new ArgumentException("A formal/registered anchor and validated image transform are required.");
        var origin = anchorToCurrent.Inverse().Apply(new(currentWidth / 2d, currentHeight / 2d));
        var center = PixelToSky(anchor, anchorWidth, anchorHeight, solverIdentity, origin);
        var result = new PlateSolveResult(anchor.SolveTime)
        {
            Success = true, Coordinates = center, Pixscale = anchor.Pixscale / anchorToCurrent.Scale,
            PositionAngle = anchor.PositionAngle, Flipped = anchor.Flipped,
            Radius = Math.Sqrt(currentWidth * (double)currentWidth + currentHeight * (double)currentHeight) *
                anchor.Pixscale / anchorToCurrent.Scale / 7200,
        };
        // Fit the projection convention numerically, including the NINA PL3 complement.
        // This avoids assuming the sign of screen-Y or solver angle conventions.
        var probe = PixelToSky(anchor, anchorWidth, anchorHeight, solverIdentity, new(origin.X + 100, origin.Y));
        var expected = anchorToCurrent.Apply(new(origin.X + 100, origin.Y));
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var point = G3WcsTargetProjector.Project(probe, result, currentWidth, currentHeight, solverIdentity);
            var error = new PixelPoint(point.X - expected.X, point.Y - expected.Y);
            if (Math.Sqrt(error.X * error.X + error.Y * error.Y) < 1e-5) break;
            result.PositionAngle += .001;
            var plus = G3WcsTargetProjector.Project(probe, result, currentWidth, currentHeight, solverIdentity);
            result.PositionAngle -= .001;
            var dx = (plus.X - point.X) / .001; var dy = (plus.Y - point.Y) / .001;
            var norm = dx * dx + dy * dy;
            if (!double.IsFinite(norm) || norm < 1e-12) throw new InvalidOperationException("Registered WCS orientation is singular.");
            result.PositionAngle -= Math.Clamp((dx * error.X + dy * error.Y) / norm, -10, 10);
        }
        result.PositionAngle = (result.PositionAngle % 360 + 360) % 360;
        // A bounded similarity should remain accurate throughout this small field. A fit
        // that cannot reproduce an independent off-axis point is not promoted as WCS.
        foreach (var offset in new[] { new PixelPoint(100, 0), new(0, 100), new(-100, -100) })
        {
            var reference = new PixelPoint(origin.X + offset.X, origin.Y + offset.Y);
            var sky = PixelToSky(anchor, anchorWidth, anchorHeight, solverIdentity, reference);
            var projected = G3WcsTargetProjector.Project(sky, result, currentWidth, currentHeight, solverIdentity);
            var measured = anchorToCurrent.Apply(reference);
            if (Math.Sqrt(Math.Pow(projected.X - measured.X, 2) + Math.Pow(projected.Y - measured.Y, 2)) > .2)
                throw new InvalidOperationException("Registered WCS representation exceeds its bounded projection residual.");
        }
        return result;
    }

    internal static PlateSolveResult RestoreFromMosaic(PlateSolveResult mosaicSolve, G3MosaicComposition mosaic,
        string solverIdentity, G3SimilarityTransform referenceToCurrent, int currentWidth, int currentHeight) =>
        RestoreCurrentResult(mosaicSolve, mosaic.Width, mosaic.Height, solverIdentity,
            new G3SimilarityTransform(1, 0, mosaic.ReferencePixelOffset.X, mosaic.ReferencePixelOffset.Y).Then(referenceToCurrent),
            currentWidth, currentHeight);

    private static Coordinates PixelToSky(PlateSolveResult anchor, int width, int height, string identity, PixelPoint pixel)
    {
        var ra = anchor.Coordinates.RADegrees; var dec = anchor.Coordinates.Dec;
        const double step = 1d / 3600;
        PixelPoint Project(double rightAscension, double declination) => G3WcsTargetProjector.Project(
            new Coordinates((rightAscension % 360 + 360) % 360, Math.Clamp(declination, -89.99, 89.99),
                anchor.Coordinates.Epoch, Coordinates.RAType.Degrees), anchor, width, height, identity);
        for (var iteration = 0; iteration < 30; iteration++)
        {
            var p = Project(ra, dec); var ex = p.X - pixel.X; var ey = p.Y - pixel.Y;
            if (Math.Sqrt(ex * ex + ey * ey) <= 1e-6)
                return new Coordinates((ra % 360 + 360) % 360, dec, anchor.Coordinates.Epoch, Coordinates.RAType.Degrees);
            var r = Project(ra + step, dec); var d = Project(ra, dec + step);
            var a = (r.X - p.X) / step; var b = (d.X - p.X) / step;
            var c = (r.Y - p.Y) / step; var e = (d.Y - p.Y) / step;
            var determinant = a * e - b * c;
            if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-9) break;
            ra -= Math.Clamp((ex * e - ey * b) / determinant, -.5, .5);
            dec -= Math.Clamp((a * ey - c * ex) / determinant, -.5, .5);
            if (dec is < -89.9 or > 89.9) break;
        }
        throw new InvalidOperationException("Image registration WCS inversion did not converge.");
    }
}
