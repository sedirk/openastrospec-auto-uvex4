using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;
public sealed class SepMainFocusTests
{
    private static SepMainFocusOptions Options => new() { MinimumPosition = 4500, MaximumPosition = 5500 };

    [Fact]
    public async Task InvalidConfigurationIsRejectedBeforeContactingAnyHardware()
    {
        var fake = new Fake();
        await Assert.ThrowsAsync<ArgumentException>(() => new SepMainFocusRunner(fake).RunAsync(new(),
            Path.Combine(Path.GetTempPath(), "invalid-sep-focus-" + Guid.NewGuid()), null, CancellationToken.None));
        Assert.Equal(0, fake.Reads);
        Assert.Equal(0, fake.Captures);
        Assert.Single(fake.Moves);
    }

    [Fact]
    public void ConfigurationExplainsTheActualInvalidField()
    {
        Assert.Contains("最小、最大", new SepMainFocusOptions().ConfigurationIssue!);
        Assert.Contains("间隔", (Options with { Step=0 }).ConfigurationIssue!);
        Assert.Contains("曝光", (Options with { ExposureMilliseconds=100 }).ConfigurationIssue!);
        Assert.Contains("饱和值", (Options with { Saturation=double.NaN }).ConfigurationIssue!);
        Assert.Contains("高度", (Options with { MinimumAltitude=double.PositiveInfinity }).ConfigurationIssue!);
        Assert.Null(Options.ConfigurationIssue);
    }

    [Fact]
    public async Task LiveCurveUsesSweepOnlyAndDoesNotMixNewAbabReferenceIds()
    {
        var points = new List<SepFocusPoint[]>();
        var result = await new SepMainFocusRunner(new Fake()).RunAsync(Options,
            Path.Combine(Path.GetTempPath(), "sep-focus-progress-" + Guid.NewGuid()), null, CancellationToken.None,
            new InlineProgress<SepFocusPoint[]>(points.Add));
        Assert.True(result.Improved);
        Assert.Equal(6, points.Count); // Five sweep groups + final summary, never the baseline.
        Assert.Equal(result.Curve, points.Last());
        Assert.DoesNotContain(points.SelectMany(p => p), p => p.Position == 4975); // Candidate belongs to a separate reference set.
        Assert.All(points.SelectMany(p => p), p => Assert.True(p.R50 > 0 && p.Frames >= 2));
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }

    [Fact]
    public void LimitsIncludeNativeBacklashAndNeverAssumeSite5000()
    {
        Assert.Throws<ArgumentException>(() => new SepMainFocusOptions().Positions(5000, 100, 0));
        Assert.Throws<InvalidOperationException>(() => Options.Positions(4650, 100, 0));
        Assert.Equal(new[] { 3100, 3150, 3200, 3250, 3300 }, (Options with { MinimumPosition=2900,MaximumPosition=3500 }).Positions(3200,100,0));
        Assert.Throws<InvalidOperationException>(() => Options.Positions(int.MaxValue, 100, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedRunnerPersistsAndRequiresRepeatedImprovement(bool flat)
    {
        var fake = new Fake { Flat = flat };
        var path = Path.Combine(Path.GetTempPath(), "sep-main-focus-test-" + Guid.NewGuid());
        var result = await new SepMainFocusRunner(fake).RunAsync(Options, path, null, CancellationToken.None);
        Assert.True(result.ReturnConfirmed);
        Assert.Equal(flat ? 5000 : 4975, fake.Position);
        Assert.Equal(!flat, result.Improved);
        Assert.True(result.FocusVerified);
        if (flat) { Assert.Equal("VerifiedAtOrigin", result.Status); Assert.True(File.Exists(Path.Combine(path, "origin-verification.json"))); }
        Assert.True(File.Exists(Path.Combine(path,"result.json")));
        Assert.Equal(5, result.Curve.Length);
        Assert.All(fake.Moves.Zip(fake.Moves.Skip(1)), pair => Assert.InRange(Math.Abs(pair.First-pair.Second),0,150));
    }

    [Fact]
    public async Task CancellationReturnsOriginButExternalMovementIsNotOverwritten()
    {
        foreach (var external in new[] {false,true})
        {
            var fake = new Fake { CancelAtCapture=6, ExternalMove=external };
            var result = await new SepMainFocusRunner(fake).RunAsync(Options,
                Path.Combine(Path.GetTempPath(), "sep-main-focus-test-" + Guid.NewGuid()), null, CancellationToken.None);
            Assert.Equal(!external, result.ReturnConfirmed);
            Assert.False(result.Improved);
            if (!external) Assert.Equal(5000,fake.Position);
            else Assert.NotEqual(5000,fake.Position);
        }
    }

    [Fact]
    public async Task BadNativeFitNeverAppliesCandidateAndKeepsOriginalPosition()
    {
        var fake = new Fake { FitQuality = .6 };
        var result = await new SepMainFocusRunner(fake).RunAsync(Options,
            Path.Combine(Path.GetTempPath(), "sep-main-focus-test-" + Guid.NewGuid()), null, CancellationToken.None);
        Assert.Equal("Failed", result.Status);
        Assert.False(result.Improved);
        Assert.True(result.ReturnConfirmed);
        Assert.Equal(5000, fake.Position);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void MissingStarsAndNonFiniteMeasurementsNeverBecomeZeroFocus()
    {
        Assert.Throws<InvalidDataException>(() => new SepFocusFrame([new(2,2)], [new(0,2,2,double.NaN,3,100,20)]).Validate(20,20));
        Assert.Throws<InvalidOperationException>(() => SepMainFocusRunner.Summarize([new(0,5000,"raw.fit",new([],[]))]));
        Assert.False(SepMainFocusRunner.AcceptRepeatedValidation([
            new(5000,5,8,.2,3),new(4975,4.6,7,.2,3),new(5000,5,8,.2,3),new(4975,4.96,8,.2,3)]));
    }

    private static SepFocusSample Sample(int group, params int[] ids) => new(group, 4900 + group * 50, "immutable.fit",
        new([], ids.Select(i => new SepFocusStar(i, 50, 50, 4 + i, 8 + i, 1000, 30)).ToArray()));

    [Fact]
    public void VerifiedOriginRequiresNearMinimumAndFreshStableReturnNotMereRetention()
    {
        var points = new[] {5000,5003,5000,5003,5000}.Select(p => new SepFocusPoint(p,5,8,.15,3)).ToArray();
        Assert.True(SepMainFocusRunner.AcceptVerifiedOrigin(points,5000,5003,50));
        Assert.False(SepMainFocusRunner.AcceptVerifiedOrigin(points.Take(4).ToArray(),5000,5003,50));
        Assert.False(SepMainFocusRunner.AcceptVerifiedOrigin(points,5000,5030,50));
        points[4] = points[4] with { R50 = 6 };
        Assert.False(SepMainFocusRunner.AcceptVerifiedOrigin(points,5000,5003,50));
        points[4] = points[4] with { R50 = 5, R80 = 9 };
        Assert.False(SepMainFocusRunner.AcceptVerifiedOrigin(points,5000,5003,50));
        var retained = new SepMainFocusResult("Unchanged",5000,5000,5003,false,true,"retained","unused",[],[]);
        Assert.False(retained.FocusVerified);
        Assert.False((retained with { Status="Failed" }).FocusVerified);
        Assert.False((retained with { Status="VerifiedAtOrigin", ReturnConfirmed=false }).FocusVerified);
    }

    [Fact]
    public void OneDroppedStarInThirdFrameDoesNotDiscardTwoCompleteFrames()
    {
        var samples = new[] { Sample(1,0,1,2,3), Sample(1,0,1,2,3), Sample(1,0,4,5),
            Sample(2,0,1,2), Sample(2,0,1,2), Sample(2,3,4,5) };
        var curve = SepMainFocusRunner.Summarize(samples);
        Assert.All(curve, point => { Assert.Equal(2, point.Frames); Assert.Equal(5, point.R50); });
    }

    [Fact]
    public void OriginVerificationAnchorsFreshFinalPositionAndDoesNotRequireWorseCandidatesToAgree()
    {
        SepFocusPoint[] points = [new(5000,5.2,8.2,.15,3),new(5010,6,10,.15,3),
            new(5000,4.8,7.8,.15,3),new(5010,6.1,10.1,.15,3),new(5000,5,8,.15,3)];
        Assert.True(SepMainFocusRunner.AcceptVerifiedOrigin(points,5000,5010,50));
        points[1] = points[1] with { R50 = 4, R80 = 7 };
        Assert.False(SepMainFocusRunner.AcceptVerifiedOrigin(points,5000,5010,50)); // Better candidate cannot be hidden by the worse B.
        points[1] = points[1] with { R50 = 6, R80 = 10 };
        points[0] = points[0] with { R50 = 7 };
        Assert.False(SepMainFocusRunner.AcceptVerifiedOrigin(points,5000,5010,50)); // Must reproduce BOTH prior origins.
    }

    [Fact]
    public void PairwiseCoverageWithoutThreeCoObservedStarsCannotInventAnEnsemble()
    {
        Assert.Throws<InvalidOperationException>(() => SepMainFocusRunner.Summarize([
            Sample(1,0,1,2), Sample(1,0,1,3), Sample(1,0,2,3)]));
        Assert.Throws<InvalidOperationException>(() => SepMainFocusRunner.Summarize([
            Sample(1,0,1,2), Sample(1,0,1,2), Sample(2,3,4,5), Sample(2,3,4,5)]));
    }

    [Fact]
    public async Task BaselineOutlierIsPreservedButNeverPassedToNativeFit()
    {
        var fake = new Fake { BaselineOffset = 100 };
        var result = await new SepMainFocusRunner(fake).RunAsync(Options,
            Path.Combine(Path.GetTempPath(), "sep-baseline-" + Guid.NewGuid()), null, CancellationToken.None);
        Assert.True(result.Improved);
        Assert.All(result.Samples.Where(s => s.Group == 0), s => Assert.All(s.Measurement.Stars, star => Assert.True(star.R50 > 100)));
        Assert.Equal(5, fake.FitPoints!.Length);
        Assert.Single(fake.FitPoints, p => p.Position == 5000);
        Assert.All(fake.FitPoints, p => Assert.True(p.R50 < 100));
    }

    private sealed class Fake : ISepMainFocusHardware
    {
        public int Position=5000, Captures, CancelAtCapture, Reads;
        public bool Flat, ExternalMove;
        public double FitQuality = .95;
        public double BaselineOffset;
        public SepFocusPoint[]? FitPoints;
        public List<int> Moves = [5000];
        public Task<SepFocusState> ReadReadyAsync(CancellationToken token) { Reads++; return Task.FromResult(new SepFocusState(Position,"same",100,0)); }
        public Task MoveAsync(int position,CancellationToken token) { token.ThrowIfCancellationRequested(); Position=position; Moves.Add(position); return Task.CompletedTask; }
        public Task<SepFocusFrame> CaptureAsync(string path,SepFocusReference[]? reference,CancellationToken token)
        {
            if (++Captures==CancelAtCapture) { if(ExternalMove) Position++; throw new OperationCanceledException(); }
            var r50=Flat?4:3+Math.Pow(Position-4975,2)/2000;
            if (Captures <= 3) r50 += BaselineOffset;
            return Task.FromResult(new SepFocusFrame(reference ?? [new(30,30),new(90,30),new(30,90)],
                Enumerable.Range(0,3).Select(i=>new SepFocusStar(i,30,30,r50,r50+2,1000,30)).ToArray()));
        }
        public Task<SepFocusFit> FitAsync(SepFocusPoint[] points,CancellationToken token) { FitPoints = points; return Task.FromResult(new SepFocusFit(4975,FitQuality)); }
    }
}
