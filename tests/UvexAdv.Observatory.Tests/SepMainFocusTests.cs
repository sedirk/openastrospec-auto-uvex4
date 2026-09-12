using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;
public sealed class SepMainFocusTests
{
    private static SepMainFocusOptions Options => new() { MinimumPosition = 4500, MaximumPosition = 5500 };

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
        Assert.True(File.Exists(Path.Combine(path,"result.json")));
        Assert.Equal(6, result.Curve.Length);
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

    private sealed class Fake : ISepMainFocusHardware
    {
        public int Position=5000, Captures, CancelAtCapture;
        public bool Flat, ExternalMove;
        public double FitQuality = .95;
        public List<int> Moves = [5000];
        public Task<SepFocusState> ReadReadyAsync(CancellationToken token) => Task.FromResult(new SepFocusState(Position,"same",100,0));
        public Task MoveAsync(int position,CancellationToken token) { token.ThrowIfCancellationRequested(); Position=position; Moves.Add(position); return Task.CompletedTask; }
        public Task<SepFocusFrame> CaptureAsync(string path,SepFocusReference[]? reference,CancellationToken token)
        {
            if (++Captures==CancelAtCapture) { if(ExternalMove) Position++; throw new OperationCanceledException(); }
            var r50=Flat?4:3+Math.Pow(Position-4975,2)/2000;
            return Task.FromResult(new SepFocusFrame(reference ?? [new(30,30),new(90,30),new(30,90)],
                Enumerable.Range(0,3).Select(i=>new SepFocusStar(i,30,30,r50,r50+2,1000,30)).ToArray()));
        }
        public Task<SepFocusFit> FitAsync(SepFocusPoint[] points,CancellationToken token) => Task.FromResult(new SepFocusFit(4975,FitQuality));
    }
}
