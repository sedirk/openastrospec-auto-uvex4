using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class SparseAvailablePixelWorkflowTests
{
    [Fact]
    public void AnIndependentlyBoundedExposureMaySupplementAShortWaitWithoutRetimestampingTheReadback()
    {
        var waitRequested = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
        var actualCompleted = waitRequested.AddMilliseconds(100);
        var now = actualCompleted.AddMilliseconds(50);
        var afterWaitOnly = new G3SparseExposureTiming(waitRequested, actualCompleted, 2, now);
        Assert.False(RealObservationStageRunner.ValidRetainedSparseCaptureTiming(
            afterWaitOnly, actualCompleted, 2000, now));
        // This bound must be supplied by its owner. A rolling save request
        // without that information uses the explicit UNKNOWN case below.
        var possibleExposure = afterWaitOnly with { CaptureRequestedUtc = waitRequested.AddSeconds(-2) };
        Assert.True(RealObservationStageRunner.ValidRetainedSparseCaptureTiming(
            possibleExposure, actualCompleted, 2000, now));
        Assert.True(G3SparseFrameTimingPolicy.Evaluate(possibleExposure, now,
            G3SparseTimingPurpose.RegisteredCurrentFrame, 1, 100).Usable);
        Assert.Equal(actualCompleted, possibleExposure.CaptureCompletedUtc);
        Assert.Equal(waitRequested.AddSeconds(-1), possibleExposure.EarliestMidpointUtc);
        Assert.Equal(waitRequested, afterWaitOnly.CaptureRequestedUtc); // Actual readback is not rewritten.
        Assert.False(RealObservationStageRunner.ValidRetainedSparseCaptureTiming(
            possibleExposure with { CaptureCompletedUtc = actualCompleted.AddSeconds(1) }, actualCompleted, 2000, now));
        Assert.False(RealObservationStageRunner.ValidRetainedSparseCaptureTiming(
            possibleExposure with { ExposureSeconds = 1 }, actualCompleted, 2000, now));
        Assert.False(RealObservationStageRunner.ValidRetainedSparseCaptureTiming(
            possibleExposure with { SolveCompletedUtc = now.AddSeconds(1) }, actualCompleted, 2000, now));
    }

    [Fact]
    public void UnknownRollingShutterStartKeepsRawCompletionButCannotEnterATimedGraph()
    {
        var completed = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
        var current = new G3SparseExposureTiming(completed.AddSeconds(-3), completed, 2, completed.AddSeconds(1));
        var retained = RealObservationStageRunner.RetainedSparseCaptureTimingForGraph(current, captureTimingKnown: false);
        Assert.Equal(default, retained.CaptureRequestedUtc);
        Assert.Equal(current.CaptureCompletedUtc, retained.CaptureCompletedUtc);
        Assert.Equal(current.ExposureSeconds, retained.ExposureSeconds);
        Assert.False(RealObservationStageRunner.ValidRetainedSparseCaptureTiming(retained, completed, 2000, completed.AddSeconds(2)));
        foreach (var purpose in Enum.GetValues<G3SparseTimingPurpose>())
            Assert.False(G3SparseFrameTimingPolicy.Evaluate(retained, completed.AddSeconds(2), purpose,
                1, 100, measuredDrift: new(.1, "other-measured-image-pair")).Usable);
        Assert.False(G3SparseGraphWindowPolicy.Evaluate(current, retained, [retained],
            completed.AddSeconds(2), 100).Usable);
        Assert.Equal(current, RealObservationStageRunner.RetainedSparseCaptureTimingForGraph(current, captureTimingKnown: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingConnectedPixelsActuallyReachMosaicBeforeAnyNewDirectSolveOrQhyWitness(bool unusableAnchorExists)
    {
        var calls = new List<string>();
        var attempted = new HashSet<string>();
        var inputs = Inputs();
        var result = await G3SparseRecoveryWorkflow.TryAvailableBeforeSolveAsync(
            _ => { calls.Add("validate"); return Task.CompletedTask; },
            _ => { calls.Add("retain-current"); return Task.CompletedTask; },
            _ => { calls.Add(unusableAnchorExists ? "anchor-rejected" : "no-anchor"); return Task.FromResult<PlateSolveEvidence?>(null); },
            _ => throw new Xunit.Sdk.XunitException("Do not request QHY before an available pixel mosaic."),
            _ =>
            {
                Assert.True(G3SparseRecoveryWorkflow.TryBeginMosaic(inputs.Select(i => i.SourceSha256).ToArray(), attempted));
                calls.Add("compose-real-pixels");
                var composition = G3MosaicComposer.Compose(inputs);
                Assert.Equal(2, composition.SourceContributions.Count);
                calls.Add("finite-mosaic-solver");
                var solved = new PlateSolveResult { Success = true, Pixscale = .38, PositionAngle = 260,
                    Coordinates = new Coordinates(20, 30, Epoch.J2000, Coordinates.RAType.Degrees) };
                var current = G3RegisteredWcs.RestoreFromMosaic(solved, composition, "Platesolve3Solver",
                    inputs[^1].ReferenceToFrame, 640, 480);
                return Task.FromResult<PlateSolveEvidence?>(new(current, solved.Coordinates, 0,
                    "Platesolve3Solver", "new-current.fit", "derived-mosaic-solve.json", new('C', 64), 640, 480, 1,
                    DateTimeOffset.UtcNow) { Authority = "RegisteredMosaicWcs", PredictionUncertaintyPixels = 4 });
            }, default);
        Assert.NotNull(result);
        Assert.False(result.IsFormal);
        Assert.Equal("RegisteredMosaicWcs", result.Authority);
        Assert.Single(calls, c => c == "retain-current");
        Assert.Single(calls, c => c == "finite-mosaic-solver");
        Assert.Equal("validate", calls[^1]);
        Assert.DoesNotContain("direct-solve", calls);
        // Reordering this exact set, or adding/removing an unusable anchor,
        // cannot grant the same pixels a second formal solve attempt.
        Assert.False(G3SparseRecoveryWorkflow.TryBeginMosaic(inputs.Select(i => i.SourceSha256).Reverse().ToArray(), attempted));
    }

    [Fact]
    public async Task PreSolveMosaicCannotEscapeLateUnsafeOrStartAnotherFallback()
    {
        var changed = false;
        var calls = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => G3SparseRecoveryWorkflow.TryAvailableBeforeSolveAsync(
            _ => changed ? Task.FromException(new InvalidOperationException("Unsafe after image work")) : Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.FromResult<StageResult?>(null),
            _ => throw new Xunit.Sdk.XunitException("No later QHY operation after Unsafe"),
            _ => { calls.Add("mosaic"); changed = true; return Task.FromResult<StageResult?>(new(GateResult.Warn("INFERRED", "Not yet target acceptance"))); }, default));
        Assert.Equal(["mosaic"], calls);
    }

    [Fact]
    public async Task CancellationBeforeRetainingPixelsCannotStartAnyFallback()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => G3SparseRecoveryWorkflow.TryAvailableBeforeSolveAsync<StageResult>(
            _ => throw new Xunit.Sdk.XunitException("No validation after cancellation"),
            _ => throw new Xunit.Sdk.XunitException("No retention after cancellation"),
            _ => throw new Xunit.Sdk.XunitException("No registration after cancellation"),
            _ => throw new Xunit.Sdk.XunitException("No QHY after cancellation"),
            _ => throw new Xunit.Sdk.XunitException("No mosaic after cancellation"), cancel.Token));
    }

    [Fact]
    public void ANewRawFrameButNotARepeatedSourceSetPermitsAnotherBoundedMosaic()
    {
        var attempted = new HashSet<string>();
        Assert.False(G3SparseRecoveryWorkflow.TryBeginMosaic([new('A', 64)], attempted));
        Assert.False(G3SparseRecoveryWorkflow.TryBeginMosaic([new('A', 64), new('A', 64)], attempted));
        Assert.True(G3SparseRecoveryWorkflow.TryBeginMosaic([new('A', 64), new('B', 64)], attempted));
        Assert.False(G3SparseRecoveryWorkflow.TryBeginMosaic([new('B', 64), new('A', 64)], attempted));
        Assert.False(G3SparseRecoveryWorkflow.TryBeginMosaic([new('b', 64), new('a', 64)], attempted));
        Assert.True(G3SparseRecoveryWorkflow.TryBeginMosaic([new('A', 64), new('B', 64), new('C', 64)], attempted));
    }

    private static G3MosaicInput[] Inputs()
    {
        PixelPoint[] field = [new(80, 85), new(220, 65), new(390, 180), new(130, 320), new(280, 410), new(480, 330), new(490, 90)];
        StarCandidate[] Stars(IEnumerable<PixelPoint> points) => points.Select((p, i) => new StarCandidate(p, 1000, 2000, 100 - i, 3, .1, 0, 20)).ToArray();
        var move = new G3SimilarityTransform(1, 0, 37, 15);
        var registration = G3OverlapRegistration.Register(Stars(field), Stars(field.Select(move.Apply)), 640, 480);
        Assert.True(registration.Accepted, registration.Reason);
        Assert.True(registration.HoldoutPairs.Count >= 2);
        MonochromeFrame Frame(IEnumerable<PixelPoint> stars)
        {
            var pixels = Enumerable.Repeat((ushort)1000, 640 * 480).ToArray();
            foreach (var star in stars) pixels[(int)star.Y * 640 + (int)star.X] = 3000;
            return new(640, 480, pixels, 65535);
        }
        return [new(Frame(field), G3SimilarityTransform.Identity, "retained.fit", new('A', 64)),
            new(Frame(field.Select(move.Apply)), registration.Transform!, "new-current.fit", new('B', 64))];
    }
}
