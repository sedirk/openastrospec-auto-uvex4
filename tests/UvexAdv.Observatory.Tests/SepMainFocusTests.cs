using System.Text.Json;
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
        using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "validation.json")));
        Assert.Equal(2, evidence.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(!flat, evidence.RootElement.GetProperty("paired").GetProperty("Improved").GetBoolean());
        Assert.Equal(4, evidence.RootElement.GetProperty("validation").GetArrayLength());
        Assert.Equal(4, evidence.RootElement.GetProperty("paired").GetProperty("Groups").GetArrayLength());
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

    [Theory]
    [InlineData(.6)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task BadNativeFitNeverAppliesCandidateAndKeepsOriginalPosition(double quality)
    {
        var fake = new Fake { FitQuality = quality };
        var result = await new SepMainFocusRunner(fake).RunAsync(Options,
            Path.Combine(Path.GetTempPath(), "sep-main-focus-test-" + Guid.NewGuid()), null, CancellationToken.None);
        Assert.Equal("Failed", result.Status);
        Assert.False(result.Improved);
        Assert.True(result.ReturnConfirmed);
        Assert.Equal(5000, fake.Position);
        Assert.Null(result.Candidate);
        Assert.Contains("拟合优度 R²", result.Message);
        Assert.DoesNotContain("外推", result.Message);
        Assert.Equal(Options.Frames * (Options.HalfPoints * 2 + 2), fake.Captures);
    }

    [Theory]
    [InlineData(4875, "较小焦位侧", "外推估计")]
    [InlineData(4900, "较小焦位侧", "边界")]
    [InlineData(5100, "较大焦位侧", "边界")]
    [InlineData(5125, "较大焦位侧", "外推估计")]
    public async Task OutOfRangeOrBoundaryMinimumIsNotAppliedOrValidated(int fittedPosition, string side, string reason)
    {
        var fake = new Fake { FitPosition = fittedPosition, FitQuality = .978 };
        var directory = Path.Combine(Path.GetTempPath(), "sep-unbracketed-fit-" + Guid.NewGuid());
        var result = await new SepMainFocusRunner(fake).RunAsync(Options, directory, null, CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        Assert.False(result.Improved);
        Assert.False(result.FocusVerified);
        Assert.Null(result.Candidate);
        Assert.True(result.ReturnConfirmed);
        Assert.Equal(5000, result.FinalPosition);
        Assert.Equal(5000, fake.Position);
        Assert.Equal(new[] { 5000, 4900, 4950, 5000, 5050, 5100, 5000 }, fake.Moves);
        Assert.Equal(Options.Frames * (Options.HalfPoints * 2 + 2), fake.Captures);
        Assert.All(result.Samples, sample => Assert.InRange(sample.Group, 0, Options.HalfPoints * 2 + 1));
        Assert.Contains("4900–5100", result.Message);
        Assert.Contains(fittedPosition.ToString(), result.Message);
        Assert.Contains(reason, result.Message);
        Assert.Contains(side, result.Message);
        Assert.Contains("不能据此声称最佳焦点", result.Message);
        Assert.Contains("已回到原位 5000", result.Message);
        Assert.Contains("不自动扩界", result.Message);
        Assert.DoesNotContain("拟合优度 R²", result.Message);
        Assert.True(File.Exists(Path.Combine(directory, "native-fit.json")));
        Assert.False(File.Exists(Path.Combine(directory, "validation.json")));
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
    public void PairedValidationDoesNotConfuseRadiusRankChangesWithLackOfImprovement()
    {
        var samples = PairedSamples();
        var paired = SepMainFocusRunner.CompareRepeatedSamples(samples);
        Assert.False(SepMainFocusRunner.AcceptRepeatedValidation(paired.AbsolutePoints));
        Assert.Equal(5, paired.AbsolutePoints[0].R50);
        Assert.Equal(5.2, paired.AbsolutePoints[1].R50);
        Assert.Equal(SepMainFocusRunner.Summarize(samples), paired.AbsolutePoints);
        Assert.True(paired.Improved);
        Assert.Equal(Enumerable.Range(0, 9), paired.StarIds);
        Assert.Equal(new[] { 5, 2, 5, 3 }, paired.Groups.Select(g => g.FramePaths.Length));
        Assert.Equal(new[] { "g1-f3.fit", "g1-f4.fit" }, paired.Groups[1].FramePaths);
        Assert.Equal(new[] { "g3-f0.fit", "g3-f3.fit", "g3-f4.fit" }, paired.Groups[3].FramePaths);
        Assert.Equal(7.4 / 8.2, paired.Pairs[0].R50Ratio, 12);
        Assert.Equal(4.6 / 5.1, paired.Pairs[1].R50Ratio, 12);
        Assert.Equal(9.4 / 9.2, paired.Pairs[0].R80Ratio, 12);
        Assert.Equal(7.4 / 7.9, paired.Pairs[1].R80Ratio, 12);
        Assert.All(paired.Pairs, p => { Assert.True(p.Accepted); Assert.Equal(9, p.Stars.Length); });
        Assert.Equal(samples[0].Measurement.Reference, paired.Reference);
    }

    [Fact]
    public void PairedValidationIsInvariantToIndependentStarSizeScalesAndStarOrder()
    {
        var samples = PairedSamples();
        var original = SepMainFocusRunner.CompareRepeatedSamples(samples);
        var scaled = samples.Select(s => s with { Measurement = s.Measurement with
        {
            Stars = s.Measurement.Stars.Reverse().Select(star => star with
            { R50 = star.R50 * (star.Id + 1), R80 = star.R80 * (star.Id + 1) }).ToArray()
        } }).ToArray();
        var actual = SepMainFocusRunner.CompareRepeatedSamples(scaled);
        Assert.Equal(original.Improved, actual.Improved);
        Assert.Equal(original.StarIds, actual.StarIds);
        for (var pair = 0; pair < 2; pair++)
        {
            Assert.Equal(original.Pairs[pair].R50Ratio, actual.Pairs[pair].R50Ratio, 12);
            Assert.Equal(original.Pairs[pair].R80Ratio, actual.Pairs[pair].R80Ratio, 12);
            for (var star = 0; star < 9; star++)
            {
                Assert.Equal(original.Pairs[pair].Stars[star].R50Ratio, actual.Pairs[pair].Stars[star].R50Ratio, 12);
                Assert.Equal(original.Pairs[pair].Stars[star].R80Ratio, actual.Pairs[pair].Stars[star].R80Ratio, 12);
            }
        }
    }

    [Fact]
    public void PairedValidationRejectsWhenAbsoluteMedianRankChangesWouldFalselyAccept()
    {
        double[] a = [1, 10, 11], b = [.99, 8, 10.9];
        var samples = PairedSamples().Select(s => s with { Measurement = s.Measurement with
        {
            Stars = s.Measurement.Stars.Where(star => star.Id < 3).Select(star =>
            {
                var radius = (s.Group % 2 == 0 ? a : b)[star.Id];
                return star with { R50 = radius, R80 = radius * 2 };
            }).ToArray()
        } }).ToArray();
        var result = SepMainFocusRunner.CompareRepeatedSamples(samples);
        Assert.True(SepMainFocusRunner.AcceptRepeatedValidation(result.AbsolutePoints));
        Assert.False(result.Improved);
        Assert.All(result.Pairs, pair => { Assert.Equal(.99, pair.R50Ratio, 12); Assert.False(pair.Accepted); });
    }

    [Fact]
    public async Task SharedRunnerAppliesPairedDecisionAndPersistsItsActualSelectedEvidence()
    {
        var repeated = PairedSamples().Select(s => s with { Position = s.Group % 2 == 0 ? 5000 : 4975 }).ToArray();
        var fake = new Fake { RepeatedSamples = repeated, RepeatedStartCapture = 30 };
        var directory = Path.Combine(Path.GetTempPath(), "sep-paired-runner-" + Guid.NewGuid());
        var result = await new SepMainFocusRunner(fake).RunAsync(Options with { Frames = 5 }, directory, null, CancellationToken.None);
        Assert.Equal("Improved", result.Status);
        Assert.True(result.FocusVerified);
        Assert.Equal(4975, result.FinalPosition);
        Assert.Equal(50, fake.Captures);
        using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "validation.json")));
        var saved = evidence.RootElement.GetProperty("paired").Deserialize<SepFocusRepeatedComparison>()!;
        Assert.True(saved.Improved);
        Assert.False(SepMainFocusRunner.AcceptRepeatedValidation(saved.AbsolutePoints));
        Assert.Equal(new[] { 5, 2, 5, 3 }, saved.Groups.Select(g => g.FramePaths.Length));
        Assert.Equal(Enumerable.Range(0, 9), saved.StarIds);
        var validationSamples = result.Samples.Where(s => s.Group >= 6).ToArray();
        Assert.Equal(validationSamples.Where(s => s.Group == 7).Skip(3).Select(s => s.Path), saved.Groups[1].FramePaths);
        Assert.Equal(SepMainFocusRunner.CompareRepeatedSamples(validationSamples).Pairs[0].R50Ratio, saved.Pairs[0].R50Ratio);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void EachRepeatedPairMustIndependentlyPassBothUnchangedThresholds(int failingGroup, bool badR80)
    {
        var samples = PairedSamples();
        var a = samples.First(s => s.Group == failingGroup - 1).Measurement.Stars;
        var changed = samples.Select(s => s.Group != failingGroup ? s : s with { Measurement = s.Measurement with
        {
            Stars = s.Measurement.Stars.Select(star => badR80
                ? star with { R80 = a.Single(x => x.Id == star.Id).R80 * 1.11 }
                : star with { R50 = a.Single(x => x.Id == star.Id).R50 * .96 }).ToArray()
        } }).ToArray();
        var result = SepMainFocusRunner.CompareRepeatedSamples(changed);
        Assert.False(result.Improved);
        Assert.False(result.Pairs[failingGroup / 2].Accepted);
        Assert.True(result.Pairs[1 - failingGroup / 2].Accepted);
    }

    [Fact]
    public void PairedValidationUsesWithinStarGroupMediansBeforeRatios()
    {
        var samples = PairedSamples();
        var expected = SepMainFocusRunner.CompareRepeatedSamples(samples);
        // One outlier in a five-frame A group cannot replace its per-star median.
        samples[0] = samples[0] with { Measurement = samples[0].Measurement with
        { Stars = samples[0].Measurement.Stars.Select(s => s with { R50 = s.R50 * 10, R80 = s.R80 * 10 }).ToArray() } };
        var result = SepMainFocusRunner.CompareRepeatedSamples(samples);
        Assert.Equal(expected.Groups[0].Stars, result.Groups[0].Stars);
        Assert.Equal(expected.Pairs[0].R50Ratio, result.Pairs[0].R50Ratio);
        Assert.Equal(expected.Pairs[0].R80Ratio, result.Pairs[0].R80Ratio);
    }

    [Fact]
    public void PairedValidationRejectsInsufficientFramesOrCommonStarsWithoutFilling()
    {
        var samples = PairedSamples();
        Assert.Throws<InvalidOperationException>(() => SepMainFocusRunner.CompareRepeatedSamples(
            samples.Where(s => s.Group != 1 || s.Path == "g1-f3.fit").ToArray()));
        Assert.Throws<InvalidOperationException>(() => SepMainFocusRunner.CompareRepeatedSamples(samples.Select(s =>
            s with { Measurement = s.Measurement with { Stars = s.Measurement.Stars.Where(x => x.Id < 2).ToArray() } }).ToArray()));
        // Every frame has three IDs, but no three are co-observed in two frames.
        Assert.Throws<InvalidOperationException>(() => SepMainFocusRunner.CompareRepeatedSamples([
            Sample(1,0,1,2), Sample(1,0,1,3), Sample(1,0,2,3)]));
    }

    [Fact]
    public void PairedValidationRejectsDifferentUsableReferenceButIgnoresUninitializedMissingFrame()
    {
        var samples = PairedSamples();
        var missing = samples[0] with { Path = "uninitialized.fit", Measurement = new([], []) };
        Assert.True(SepMainFocusRunner.CompareRepeatedSamples(new[] { missing }.Concat(samples).ToArray()).Improved);
        samples[0] = samples[0] with { Measurement = samples[0].Measurement with
        { Reference = samples[0].Measurement.Reference.Select(p => p with { X = p.X + 1 }).ToArray() } };
        var error = Assert.Throws<InvalidOperationException>(() => SepMainFocusRunner.CompareRepeatedSamples(samples));
        Assert.Contains("参考星纪元", error.Message);
    }

    [Fact]
    public void PairedValidationRejectsNonAbabGroupsAndInvalidSelectedMeasurements()
    {
        var samples = PairedSamples();
        Assert.Throws<InvalidOperationException>(() => SepMainFocusRunner.CompareRepeatedSamples(
            samples.Where(s => s.Group != 3).ToArray()));
        Assert.Throws<InvalidOperationException>(() => SepMainFocusRunner.CompareRepeatedSamples(
            samples.Select(s => s.Group == 2 ? s with { Position = 1100 } : s).ToArray()));
        samples[0] = samples[0] with { Measurement = samples[0].Measurement with
        { Stars = samples[0].Measurement.Stars.Select(s => s with { R50 = double.NaN }).ToArray() } };
        Assert.Throws<InvalidDataException>(() => SepMainFocusRunner.CompareRepeatedSamples(samples));
    }

    private static SepFocusSample[] PairedSamples()
    {
        // Synthetic only: heterogeneous sizes/rank changes, not copied observing data.
        double[][] r50 = [[4.7,4.9,5,4.8,7.5,8.2,4.7,7.7,7.5], [4.6,4.1,5.1,4.7,6,7.4,5.2,6.6,6.5],
            [4.8,4.9,5.1,5.4,7.3,8.6,5,7.1,8.4], [4.5,4.2,4.6,5,6.5,7.8,4.8,5.8,6]];
        double[][] r80 = [[7.8,8.2,8.8,9.2,11.8,13.5,8.9,13.8,13], [8.2,7.3,10.9,9.4,10.8,13.4,12.5,14.5,11.7],
            [8,7.9,9.3,10.6,12.2,15.3,9.6,11.8,15.4], [8,7.4,8.6,10.3,11.6,14.1,10.8,10.3,11.4]];
        int[][][] missing = [[[],[],[],[],[]], [[0],[1],[1],[],[]], [[],[],[],[],[]], [[],[0],[1],[],[]]];
        var reference = Enumerable.Range(0, 9).Select(i => new SepFocusReference(30 + i * 50, 30)).ToArray();
        return Enumerable.Range(0, 4).SelectMany(g => Enumerable.Range(0, 5).Select(f => new SepFocusSample(g,
            g % 2 == 0 ? 1000 : 940, $"g{g}-f{f}.fit", new(reference.ToArray(), Enumerable.Range(0, 9)
                .Where(id => !missing[g][f].Contains(id)).Select(id => new SepFocusStar(id, reference[id].X,
                    reference[id].Y, r50[g][id], r80[g][id], 1000, 30)).ToArray())))).ToArray();
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

    [Fact]
    public async Task OneCompleteBaselineFrameFailsBeforeAnyFocusMotionOrFit()
    {
        var fake = new Fake { CompleteBaselineFrames = 1 };
        var result = await new SepMainFocusRunner(fake).RunAsync(Options,
            Path.Combine(Path.GetTempPath(), "sep-baseline-incomplete-" + Guid.NewGuid()), null, CancellationToken.None);

        Assert.Equal("Failed", result.Status);
        Assert.Contains("基准组", result.Message);
        Assert.Contains("未开始扫焦", result.Message);
        Assert.False(result.FocusVerified);
        Assert.True(result.ReturnConfirmed);
        Assert.Equal(5000, result.FinalPosition);
        Assert.Equal(Options.Frames, fake.Captures);
        Assert.Single(fake.Moves); // Initial position only: MoveAsync was never called.
        Assert.Null(fake.FitPoints);
        Assert.Empty(result.Curve);
        Assert.All(result.Samples, sample => Assert.Equal(0, sample.Group));
        Assert.Single(result.Samples, sample => sample.Measurement.Stars.Length >= 3);
    }

    [Fact]
    public async Task TwoCompleteBaselineFramesSupportNativeReportAndRunLocalFocusMessage()
    {
        var fake = new Fake { CompleteBaselineFrames = 2 };
        var result = await new SepMainFocusRunner(fake).RunAsync(Options,
            Path.Combine(Path.GetTempPath(), "sep-baseline-complete-" + Guid.NewGuid()), null, CancellationToken.None);

        Assert.Equal("Improved", result.Status);
        Assert.True(result.FocusVerified);
        var baseline = Assert.Single(SepMainFocusRunner.Summarize(result.Samples.Where(sample => sample.Group == 0).ToArray()));
        Assert.Equal(2, baseline.Frames);
        Assert.Equal(result.Origin, baseline.Position);
        Assert.Contains("本轮焦位锁", result.Message);
        Assert.Contains("目标/WCS/狭缝/导星", result.Message);
        Assert.Contains("不改写原 Night Setup", result.Message);
        Assert.DoesNotContain("重新建立 Night Setup", result.Message);
    }

    private sealed class Fake : ISepMainFocusHardware
    {
        public int Position=5000, Captures, CancelAtCapture, Reads;
        public bool Flat, ExternalMove;
        public double FitQuality = .95;
        public int FitPosition = 4975;
        public double BaselineOffset;
        public int CompleteBaselineFrames = 3;
        public SepFocusSample[]? RepeatedSamples;
        public int RepeatedStartCapture;
        public SepFocusPoint[]? FitPoints;
        public List<int> Moves = [5000];
        public Task<SepFocusState> ReadReadyAsync(CancellationToken token) { Reads++; return Task.FromResult(new SepFocusState(Position,"same",100,0)); }
        public Task MoveAsync(int position,CancellationToken token) { token.ThrowIfCancellationRequested(); Position=position; Moves.Add(position); return Task.CompletedTask; }
        public Task<SepFocusFrame> CaptureAsync(string path,SepFocusReference[]? reference,CancellationToken token)
        {
            if (++Captures==CancelAtCapture) { if(ExternalMove) Position++; throw new OperationCanceledException(); }
            if (RepeatedSamples is not null && Captures > RepeatedStartCapture)
                return Task.FromResult(RepeatedSamples[Captures - RepeatedStartCapture - 1].Measurement);
            var r50=Flat?4:3+Math.Pow(Position-4975,2)/2000;
            if (Captures <= 3) r50 += BaselineOffset;
            var starCount = Captures <= 3 && Captures > CompleteBaselineFrames ? 2 : 3;
            return Task.FromResult(new SepFocusFrame(reference ?? [new(30,30),new(90,30),new(30,90)],
                Enumerable.Range(0,starCount).Select(i=>new SepFocusStar(i,30,30,r50,r50+2,1000,30)).ToArray()));
        }
        public Task<SepFocusFit> FitAsync(SepFocusPoint[] points,CancellationToken token) { FitPoints = points; return Task.FromResult(new SepFocusFit(FitPosition,FitQuality)); }
    }
}
