using System.Text.Json;
using NINA.Astrometry;
using NINA.PlateSolving;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class MeasuredCoarseRecoveryTests
{
    [Theory]
    [InlineData(77.5985896522559)] [InlineData(256.4)] [InlineData(0)] [InlineData(90)]
    public void MeasuredTranslationAndFormalAxesGenerateSmallCorrectSignCorrection(double angle)
    {
        var target = new Coordinates(45.04952545264, 38.13172151748999, Epoch.J2000, Coordinates.RAType.Degrees);
        var center = new Coordinates(45.27526876111788, 38.32902926192269, Epoch.J2000, Coordinates.RAType.Degrees);
        const string solver = "NINA.PlateSolving.Solvers.Platesolve3Solver, 3.2.0.9001";
        var anchor = new PlateSolveEvidence(new PlateSolveResult { Success = true, Coordinates = center,
            Pixscale = .3821204176623478, PositionAngle = angle }, target, 0, solver,
            "formal.fit", "formal.json", new('a', 64), 1920, 1080, 1, DateTimeOffset.UtcNow);
        var measured = new PixelPoint(843.3130434782609, 344.98840579710145);
        var slit = new PixelPoint(817.6038918066614, 430.61525092821745);
        var original = new TargetIdentification(GateResult.Pass("G3_SEP_POSITION_MEASURED", "finite pair"),
            new StarCandidate(measured, 10000, 300000, 100, 4, .1, 0, 30), new(818.2895, 430.0253),
            89, 2, BoundShortPositionEvidencePath: "pair.json", CatalogPositionSpreadPixels: .8);
        var localTarget = RealObservationStageRunner.TargetInMeasuredCoarseModel(original);
        var physicalDestination = G3CatalogTargetPositionPolicy.ProjectionDestination(localTarget, slit, 100);
        Assert.Equal(slit, physicalDestination);
        Assert.Equal(measured, localTarget.PredictedPoint);
        Assert.Equal(original.BoundShortPositionEvidencePath, localTarget.BoundShortPositionEvidencePath);
        Assert.Equal(original.CatalogPositionSpreadPixels, localTarget.CatalogPositionSpreadPixels);
        Assert.NotEqual(original.PredictedPoint, localTarget.PredictedPoint);
        var model = RealObservationStageRunner.MeasuredCoarseLocalModel(anchor, target, measured, 1920, 1080);
        Assert.InRange(Distance(G3WcsTargetProjector.Project(target, model, 1920, 1080, solver), measured), 0, .001);
        var destination = G3WcsTargetProjector.SolveCenterForTargetAtPixel(target, model, 1920, 1080, solver, physicalDestination);
        Assert.InRange(G3AcquisitionMotionPlanner.AngularSeparationArcseconds(model.Coordinates.RADegrees,
            model.Coordinates.Dec, destination.DesiredG3Center.RADegrees, destination.DesiredG3Center.Dec), 34, 35);
        var derived = anchor with { Result = model, Authority = G3MeasuredCoarseRecoveryPolicy.Authority };
        Assert.False(derived.IsFormal);
        Assert.False(G3SparseRecoveryWorkflow.CanApproachDirectly(derived));
        Assert.Throws<ArgumentException>(() => RealObservationStageRunner.MeasuredCoarseLocalModel(derived, target, measured, 1920, 1080));
        // Explicit finite ideal response, NOT an image that was observed after this failed run.
        var response = new PlateSolveResult { Success = true, Coordinates = destination.DesiredG3Center,
            Pixscale = model.Pixscale, PositionAngle = model.PositionAngle };
        // The production inverse stops at 0.02 px; test its actual numerical
        // contract, not an invented one-thousandth-pixel precision guarantee.
        Assert.InRange(Distance(G3WcsTargetProjector.Project(target, response, 1920, 1080, solver), slit), 0, .02);
    }

    [Fact]
    public void OneUseCounterSurvivesRoundTripSerializationAndFamilyContinuation()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new G3AcquisitionMotionState(2, G3AcquisitionMotionState.CurrentTangentProjectionId,
            "run", new('1', 32), new('a', 64), new('b', 64), new('c', 64),
            G3AcquisitionMotionKind.WcsCentering, G3AcquisitionMotionPhase.SettledBudgetLedger,
            "pierEast", "J2000", 10, 20, 10, 20, 10, 20, 0, 0, 0, 310, 900, 3600, 12, 2, 90, 1200,
            1000, 3, now.AddMinutes(-3), now.AddMinutes(-3), now, "original.json", MeasuredPixelCorrectionAttempts: 1);
        Assert.Empty(state.Validate());
        var loaded = JsonSerializer.Deserialize<G3AcquisitionMotionState>(JsonSerializer.Serialize(state))!;
        var continued = G3AcquisitionMotionPlanner.ContinueSettledLedger(loaded, "run",
            G3AcquisitionMotionKind.LocalSearch, "fresh.json", now);
        Assert.Equal(1, continued.MeasuredPixelCorrectionAttempts);
        Assert.Equal(state.StartedUtc, continued.StartedUtc);
        Assert.Equal(state.CumulativeMotionArcseconds, continued.CumulativeMotionArcseconds);
        Assert.Equal(state.CorrectionAttempts, continued.CorrectionAttempts);
        Assert.NotEmpty((state with { MeasuredPixelCorrectionAttempts = 2 }).Validate());
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var charge = source.IndexOf("MeasuredPixelCorrectionAttempts = state.MeasuredPixelCorrectionAttempts +", StringComparison.Ordinal);
        var persist = source.IndexOf("await PersistG3AcquisitionMotionAsync", charge, StringComparison.Ordinal);
        var slew = source.IndexOf("SlewToCoordinatesAsync", charge, StringComparison.Ordinal);
        Assert.True(charge > 0 && persist > charge && slew > persist);
        Assert.Contains("lineageCopies.Max(copy => copy.MeasuredPixelCorrectionAttempts)", source);
    }

    [Theory]
    [InlineData("valid")] [InlineData("expired")] [InlineData("parent-changed")]
    [InlineData("cancel")]
    public async Task LocalModelUsesTheProductionInferenceSourceGuard(string mode)
    {
        var coordinates = new Coordinates(45, 38, Epoch.J2000, Coordinates.RAType.Degrees);
        var solve = new PlateSolveEvidence(new PlateSolveResult { Success = true, Coordinates = coordinates,
            Pixscale = .382, PositionAngle = 77.6 }, coordinates, 0, "finite",
            "short.fit", "model.json", new('a', 64), 1920, 1080, 1, DateTimeOffset.UtcNow)
        {
            Authority = G3MeasuredCoarseRecoveryPolicy.Authority,
            InferenceValidUntilUtc = DateTimeOffset.UtcNow.AddSeconds(mode == "expired" ? -1 : 30),
            InferenceSources = new Dictionary<string, string>
            { ["formal.fit"] = new('b', 64), ["short.fit"] = new('c', 64), ["model.json"] = new('a', 64) },
        };
        using var stop = new CancellationTokenSource();
        if (mode == "cancel") stop.Cancel();
        Task<GateResult> Check() => RealObservationStageRunner.ValidateSparseInferenceSourcesAsync(solve,
            (path, _) => Task.FromResult(mode == "parent-changed" && path == "formal.fit" ? new('d', 64) : solve.InferenceSources[path]), stop.Token);
        if (mode == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Check);
        else Assert.Equal(mode == "valid", (await Check()).Disposition == GateDisposition.Passed);
        Assert.False(solve.IsFormal);
    }

    [Theory]
    [InlineData("G3_MEASURED_COARSE_ALREADY_USED")]
    [InlineData("G3_MEASURED_COARSE_VECTOR_OUTSIDE")]
    public async Task LocalRecoveryQualityExhaustionRunsNextNativeTargetOnlyAfterStop(string code)
    {
        var gate = GateResult.TargetQuality(code, "Finite original-budget quality exhaustion");
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("typed-skip", code, gate,
            ObservationStage.AcquireG3SlitField);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("typed-cleanup-failure", code, gate,
            ObservationStage.AcquireG3SlitField);
    }

    private static double Distance(PixelPoint a, PixelPoint b) => Math.Sqrt(Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2));
}
