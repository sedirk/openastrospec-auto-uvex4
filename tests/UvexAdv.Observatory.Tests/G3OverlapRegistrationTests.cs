using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3OverlapRegistrationTests
{
    private static readonly PixelPoint[] Field = { new(80, 85), new(220, 65), new(390, 180),
        new(130, 320), new(280, 410), new(480, 330), new(490, 90) };
    private static StarCandidate Star(PixelPoint p, double snr = 50) => new(p, 1000, 2000, snr, 3, .1, 0, 20);
    private static StarCandidate[] Stars(IEnumerable<PixelPoint> points) => points.Select((p, i) => Star(p, 100 - i)).ToArray();

    [Fact]
    public void RealFitAndIndependentHoldoutsRecoverSimilarityNotBrightnessOrder()
    {
        var transform = new G3SimilarityTransform(Math.Cos(.02), Math.Sin(.02), 37, 15);
        var current = Stars(Field.Select(transform.Apply)).Reverse().ToArray();
        var result = G3OverlapRegistration.Register(Stars(Field), current, 640, 480);
        Assert.True(result.Accepted, result.Reason);
        Assert.Equal(3, result.FitPairs.Count);
        Assert.Equal(4, result.HoldoutPairs.Count);
        Assert.InRange(result.MaximumHoldoutResidualPixels, 0, 1e-8);
        Assert.InRange(result.OverlapFraction, .8, .95);
        Assert.Equal(transform.A, result.Transform!.A, 10);
        Assert.Equal(transform.B, result.Transform.B, 10);
        Assert.Equal(transform.X, result.Transform.X, 8);
        Assert.Equal(transform.Y, result.Transform.Y, 8);
        Assert.True(result.FitPairs.Select(p => p.Reference).Intersect(result.HoldoutPairs.Select(p => p.Reference)).Count() == 0);
    }

    [Fact]
    public void APerfectTriangleAloneCannotValidateItsOwnTransform()
    {
        var a = Field.Take(3).ToArray();
        Assert.False(G3OverlapRegistration.Register(Stars(a), Stars(a.Select(p => new PixelPoint(p.X + 5, p.Y))), 640, 480).Accepted);
    }

    [Fact]
    public void FailedIndependentHoldoutsCannotBeRefittedIntoSuccess()
    {
        var current = Field.Select((p, i) => new PixelPoint(p.X + 20 + (i >= 3 ? i * 13 : 0), p.Y + 5)).ToArray();
        Assert.False(G3OverlapRegistration.Register(Stars(Field), Stars(current), 640, 480).Accepted);
    }

    [Theory]
    [InlineData(1.1, 0)]
    [InlineData(1, 20)]
    [InlineData(-1, 0)]
    public void ScaleRotationAndReflectionOutsideModelAreNotRegistered(double scale, double degrees)
    {
        var angle = degrees * Math.PI / 180;
        var transform = new G3SimilarityTransform(scale * Math.Cos(angle), scale * Math.Sin(angle), scale < 0 ? 600 : 0, scale < 0 ? 470 : 0);
        Assert.False(G3OverlapRegistration.Register(Stars(Field), Stars(Field.Select(transform.Apply)), 640, 480).Accepted);
    }

    [Fact]
    public void TargetAndHaloCannotProvideTheMissingFieldWitnesses()
    {
        var a = Field.Take(5).ToArray();
        var exclusions = new[] { new G3RegistrationExclusion(a[4], 25) };
        Assert.False(G3OverlapRegistration.Register(Stars(a), Stars(a), 640, 480, exclusions, exclusions).Accepted);
    }

    [Fact]
    public void RepeatedIndependentPatternsDoNotGetResolvedByEnumerationOrder()
    {
        var a = new[] { new PixelPoint(50, 70), new(140, 45), new(200, 125), new(80, 180), new(170, 240) };
        var b = a.Concat(a.Select(p => new PixelPoint(p.X + 300, p.Y))).ToArray();
        var result = G3OverlapRegistration.Register(Stars(a), Stars(b), 640, 480);
        Assert.False(result.Accepted);
        Assert.Equal("G3_OVERLAP_AMBIGUOUS", result.Code);
        Assert.True(result.EquallySupportedTransforms >= 2);
    }

    [Fact]
    public void LargeAreaOverlapIsMandatoryEvenWithFiveContrivedCorrespondences()
    {
        var a = new[] { new PixelPoint(50, 40), new(80, 180), new(110, 270), new(90, 440), new(130, 360) };
        var b = a.Select(p => new PixelPoint(p.X + 490, p.Y)).ToArray();
        Assert.False(G3OverlapRegistration.Register(Stars(a), Stars(b), 640, 480).Accepted);
        Assert.InRange(G3OverlapRegistration.OverlapFraction(new(1, 0, 490, 0), 640, 480), .23, .24);
    }

    [Fact]
    public void DegenerateCollinearFieldDoesNotSupplyAPlaneRegistration()
    {
        var a = Enumerable.Range(0, 7).Select(i => new PixelPoint(50 + i * 70, 100 + i * 20)).ToArray();
        Assert.False(G3OverlapRegistration.Register(Stars(a), Stars(a.Select(p => new PixelPoint(p.X + 20, p.Y))), 640, 480).Accepted);
    }

    [Fact]
    public void CancellationPreventsAllRegistrationWork()
    {
        using var token = new CancellationTokenSource(); token.Cancel();
        Assert.Throws<OperationCanceledException>(() => G3OverlapRegistration.Register(Stars(Field), Stars(Field), 640, 480, token: token.Token));
    }

    [Fact]
    public void TransformCompositionAndInverseKeepReferencePixelMeaning()
    {
        var a = new G3SimilarityTransform(.999, .01, -50, 40);
        var b = new G3SimilarityTransform(1.001, -.02, 100, -70);
        var p = new PixelPoint(250, 170);
        var composed = a.Then(b).Apply(p); var separate = b.Apply(a.Apply(p));
        Assert.Equal(separate.X, composed.X, 10); Assert.Equal(separate.Y, composed.Y, 10);
        var restored = a.Then(b).Inverse().Apply(composed);
        Assert.Equal(p.X, restored.X, 10); Assert.Equal(p.Y, restored.Y, 10);
    }

    [Fact]
    public void ComposerUsesActualPixelsAndDoesNotChangeInputsOrInventMissingStars()
    {
        var first = Enumerable.Repeat((ushort)100, 40 * 40).ToArray();
        var second = Enumerable.Repeat((ushort)200, 40 * 40).ToArray();
        first[20 * 40 + 20] = 1000; second[20 * 40 + 10] = 1100;
        var snapshot = first.ToArray();
        var result = G3MosaicComposer.Compose(new[] {
            new G3MosaicInput(new(40, 40, first), G3SimilarityTransform.Identity, "a", new('A',64)),
            new G3MosaicInput(new(40, 40, second), new(1,0,-10,0), "b", new('B',64)) });
        Assert.Equal(50, result.Width); Assert.Equal(40, result.Height);
        Assert.Equal(1100, result.Pixels[20 * 50 + 20]);
        Assert.Equal(snapshot, first);
        Assert.Equal(1600, result.SourceContributions["a"]);
        Assert.True(result.DerivedNotRaw); Assert.Equal(1, result.CoveredFraction);
    }

    [Fact]
    public void DuplicateHashesCannotBeCountedAsIndependentImagesAndHugeCanvasIsBounded()
    {
        var frame = new MonochromeFrame(40, 40, new ushort[1600]);
        var first = new G3MosaicInput(frame, G3SimilarityTransform.Identity, "a", new('A',64));
        Assert.Throws<ArgumentException>(() => G3MosaicComposer.Compose(new[] { first, first with { SourceId = "b" } }));
        Assert.Throws<ArgumentException>(() => G3MosaicComposer.Compose(new[] { first,
            first with { SourceId = "b", SourceSha256 = new('B',64), ReferenceToFrame = new(1,0,20000,0) } }));
        Assert.Throws<ArgumentException>(() => G3MosaicComposer.Compose(new[] { first,
            first with { SourceId = "b", SourceSha256 = new('B',64), ReferenceToFrame = new(1,0,double.MaxValue,0) } }));
    }
}
