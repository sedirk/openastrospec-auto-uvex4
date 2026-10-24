using UvexAdv.Observatory;
using UvexAdv.Qhy.Core;
using System.Security.Cryptography;
using System.Text.Json;
using NINA.Astrometry;
using NINA.PlateSolving;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3SparseRecoveryContinuationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ProductionDispatcherActuallyExecutesOrderedFallbacksAndRetainsTheOriginalMiss(int successAt)
    {
        var direct = OpticalMiss();
        var recovered = new StageResult(GateResult.Warn("G3_TEST_INFERRED_NOT_FORMAL", "Independent new short-frame acceptance still required."));
        var calls = new List<string>();
        var now = DateTimeOffset.Parse("2026-10-24T00:10:00Z");
        var original = Ledger(now);
        var result = await G3SparseRecoveryWorkflow.ResolveAsync(direct, false, true,
            _ => { calls.Add("validate"); Assert.Equal(original, Ledger(now)); return Task.CompletedTask; },
            _ => { calls.Add("retain"); return Task.CompletedTask; },
            Port(1, "registered"), Port(3, "local-dual"), Port(2, "mosaic"), default);

        Assert.Same(successAt == 0 ? direct : recovered, result);
        Assert.Equal(new[] { "registered", "mosaic", "local-dual" }.Take(successAt == 0 ? 3 : successAt),
            calls.Where(c => c is "registered" or "local-dual" or "mosaic"));
        Assert.Equal("validate", calls[0]);
        Assert.Equal("retain", calls[1]);
        Assert.Equal("validate", calls[^1]);
        Assert.Equal(1, calls.Count(c => c == "retain"));
        Assert.Equal(8, original.CorrectionAttempts);
        Assert.Equal(1340, original.CumulativeMotionArcseconds);
        Assert.Equal(now.AddMinutes(-12), original.StartedUtc);

        Func<CancellationToken, Task<StageResult?>> Port(int index, string name) => _ =>
        {
            calls.Add(name);
            Assert.Equal("validate", calls[^2]);
            return Task.FromResult(successAt == index ? recovered : null);
        };
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task AlreadySolvedOrNonOpticalFailureCannotEnterTheFallbackLadder(bool accepted, bool optical)
    {
        var direct = accepted ? new StageResult(GateResult.Pass("FORMAL", "Formal current frame."))
            : new StageResult(GateResult.Unknown("PHD2_OWNER_CHANGED", "Not an optical miss."));
        var retained = 0;
        var result = await G3SparseRecoveryWorkflow.ResolveAsync(direct, accepted, optical,
            _ => Task.CompletedTask, _ => { retained++; return Task.CompletedTask; },
            Unexpected, Unexpected, Unexpected, default);
        Assert.Same(direct, result);
        Assert.Equal(accepted ? 1 : 0, retained);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LateOwnershipSafetyOrCorruptSourceCannotFallThroughToAnotherResolver(int failAt)
    {
        var calls = new List<int>();
        var expected = new InvalidDataException("An applicable immutable frame SHA no longer matches.");
        var ports = Enumerable.Range(0, 3).Select<int, Func<CancellationToken, Task<StageResult?>>>(i => _ =>
        {
            calls.Add(i);
            return i == failAt ? Task.FromException<StageResult?>(expected) : Task.FromResult<StageResult?>(null);
        }).ToArray();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => G3SparseRecoveryWorkflow.ResolveAsync(
            OpticalMiss(), false, true, _ => Task.CompletedTask, _ => Task.CompletedTask,
            ports[0], ports[2], ports[1], default));
        Assert.Same(expected, error);
        Assert.Equal(Enumerable.Range(0, failAt + 1), calls);
    }

    [Fact]
    public async Task ARecoveredResultCannotSkipLateFullValidation()
    {
        var changed = false;
        var expected = new InvalidOperationException("The original safety/owner context changed after registration.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => G3SparseRecoveryWorkflow.ResolveAsync(
            OpticalMiss(), false, true,
            _ => changed ? Task.FromException(expected) : Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => { changed = true; return Task.FromResult<StageResult?>(new(GateResult.Warn("INFERRED", "Not yet accepted."))); },
            Unexpected, Unexpected, default));
        Assert.Same(expected, error);
    }

    [Fact]
    public async Task CancellationAfterRegistrationCannotReturnItsResultOrStartADualModel()
    {
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => G3SparseRecoveryWorkflow.ResolveAsync(
            OpticalMiss(), false, true, _ => Task.CompletedTask, _ => Task.CompletedTask,
            _ => { cancel.Cancel(); return Task.FromResult<StageResult?>(new(GateResult.Warn("INFERRED", "Cancelled during measurement."))); },
            Unexpected, Unexpected, cancel.Token));
    }

    [Fact]
    public async Task OriginalReturnClockCannotBeReissuedToTheNextNonMotionFallback()
    {
        var now = DateTimeOffset.Parse("2026-10-24T00:10:00Z");
        var original = Ledger(now) with
        {
            Phase = G3AcquisitionMotionPhase.AwaitingFreshSolve,
            CommandMagnitudeArcseconds = 120,
            CurrentRaTangentOffsetArcseconds = 186.479,
            MaximumElapsedSeconds = 1200,
        };
        Assert.Empty(original.Validate());
        var ports = new List<string>();
        var error = await Assert.ThrowsAsync<G3ReturnTimeReserveException>(() => G3SparseRecoveryWorkflow.ResolveAsync(
            OpticalMiss(), false, true, _ => Task.CompletedTask, _ => Task.CompletedTask,
            _ => { ports.Add("registered"); return Task.FromResult<StageResult?>(null); },
            token => G3AcquisitionReturnTimePolicy.ExecuteAsync(
                G3AcquisitionReturnTimePolicy.Plan(original, original.StartedUtc.AddSeconds(1195)),
                _ => { ports.Add("dual"); return Task.FromResult<StageResult?>(null); }, token),
            _ => { ports.Add("mosaic"); return Task.FromResult<StageResult?>(null); }, default));
        Assert.Equal(G3AcquisitionReturnTimePolicy.ReserveCode, error.Gate.Code);
        Assert.Equal(["registered", "mosaic"], ports);
        Assert.False(error.OperationStarted);
        Assert.Equal(8, original.CorrectionAttempts);
        Assert.Equal(1340, original.CumulativeMotionArcseconds);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    [InlineData("end-policy")]
    [InlineData("late-unsafe")]
    [InlineData("late-cancel")]
    [InlineData("late-deadline")]
    [InlineData("late-dawn")]
    public async Task ExhaustedProductionSparseFallbackActuallyRunsTheNextNativeTargetOnlyAfterCheckedStop(string boundary)
    {
        var calls = new List<string>();
        var direct = OpticalMiss();
        var result = await G3SparseRecoveryWorkflow.ResolveAsync(direct, false, true,
            _ => Task.CompletedTask, _ => Task.CompletedTask,
            _ =>
            {
                calls.Add("registered");
                var registration = G3OverlapRegistration.Register([], [], 1920, 1080);
                Assert.False(registration.Accepted);
                Assert.Equal("G3_OVERLAP_UNCONFIRMED", registration.Code);
                return Task.FromResult<StageResult?>(null);
            },
            _ => { calls.Add("local-dual-unavailable"); return Task.FromResult<StageResult?>(null); },
            _ => { calls.Add("mosaic-no-connected-component"); return Task.FromResult<StageResult?>(null); }, default);
        Assert.Equal(["registered", "mosaic-no-connected-component", "local-dual-unavailable"], calls);
        Assert.Same(direct, result);
        Assert.Equal(GateFailureScope.TargetQuality, result.Gate.FailureScope);
        // The miss comes from the actual production image-ladder classifier,
        // passes the production fallback dispatcher, then executes two native
        // target/coordinator stacks. The shared fixture checks manifest joining,
        // checked stop, journal outcomes and second FinalizeObservation.
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(
            boundary, result.Gate.Code, result.Gate, ObservationStage.AcquireG3SlitField);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cleanup-failure")]
    [InlineData("end-policy")]
    [InlineData("late-unsafe")]
    [InlineData("late-cancel")]
    [InlineData("late-deadline")]
    [InlineData("late-dawn")]
    public async Task InferredGeometryWithoutAnIndependentShortTargetCannotGuideButCanSkipAfterCheckedStop(string boundary)
    {
        // The production short-position policy consumes six unmeasured images;
        // an inferred WCS/visible cursor is not an independent target position.
        var frame = new MonochromeFrame(96, 96, Enumerable.Repeat((ushort)1000, 96 * 96).ToArray(), 65535);
        var measurement = G3ShortPositionMeasurementPolicy.Measure(frame, new(48, 48), 20,
            requireResolvedPsfCandidates: true);
        var confirmation = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, measurement,
            null, new('A', 64), 6, 20, 6);
        Assert.False(confirmation.Accepted);
        Assert.False(confirmation.RetryAllowed);
        var failure = RealObservationStageRunner.CreateCatalogShortPositionFailure(
            $"Independent short confirmation exhausted: {confirmation.Gate.Code}", confirmation.Gate.Metrics);
        Assert.Equal("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", failure.Code);
        var coordinates = new Coordinates(45, 40, Epoch.J2000, Coordinates.RAType.Degrees);
        var inferred = new PlateSolveEvidence(new PlateSolveResult
        {
            Success = true, Coordinates = coordinates, Pixscale = .383,
        }, coordinates, 0, "PL3", "new-raw.fits", "inferred-parents.json", new('B', 64), 96, 96, 1, DateTimeOffset.UtcNow)
        { Authority = "RegisteredMosaicWcs", PredictionUncertaintyPixels = 4 };
        Assert.False(inferred.IsFormal);
        var field = G3FieldState.Failed(failure, "new-raw.fits", null, inferred);
        var port = StrictWorkflowPort.Create<IG3AcquisitionPort>(out var trace);
        var pass = GateResult.Pass("checked", "Finite owner port; no hardware.");
        trace.Handlers["EvaluateInterlocksAsync"] = _ => Task.FromResult(pass);
        trace.Handlers["EnsureFineAcquisitionClockStarted"] = _ => null;
        trace.Handlers["CompletePendingG3SearchReturnAsync"] = _ => Task.FromResult<StageResult?>(null);
        trace.Handlers["ReturnAfterOwnedGuidingBeforeReacquisitionAsync"] = _ => Task.FromResult(pass);
        trace.Handlers["EnsureWideToSlitTransferSkippedEvidenceAsync"] = _ => Task.FromResult("original-transfer.json");
        trace.Handlers["UsesCatalogPositionOnly"] = _ => true;
        trace.Handlers["PublishTargetBranchAsync"] = _ => Task.CompletedTask;
        trace.Handlers["CaptureAndAnalyzeG3WithSolveLadderAsync"] = _ => Task.FromResult(field);
        trace.Handlers["RememberField"] = _ => null;
        trace.Handlers["get_ActiveTargetBranch"] = _ => (TargetAcquisitionBranch?)TargetAcquisitionBranch.CatalogWcsGeometry;
        var configuration = new G3RunConfiguration(1000, 50, 1, 65535, 1000, 2.4, false, 5,
            null!, null!, 60, 120, 3, WideToSlitTransferMode.Skip, null!);
        var produced = await new G3AcquisitionWorkflow(configuration, port)
            .AcquireAsync(AtrWorkflowFixture.CreateContext(), default);
        Assert.Same(failure, produced.Gate);
        Assert.Single(trace.Calls, c => c == "CaptureAndAnalyzeG3WithSolveLadderAsync");
        Assert.DoesNotContain("AcceptField", trace.Calls);
        Assert.DoesNotContain("RunG3WcsCenteringAsync", trace.Calls);
        Assert.DoesNotContain("RunBoundedG3LocalSearchAsync", trace.Calls);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(
            boundary, produced.Gate.Code, produced.Gate, ObservationStage.AcquireG3SlitField);
    }

    [Fact]
    public void FirstFormalWcsCorrectionUsesOverlapBeforeAnyHistoricalSolveMissCanSetAFlag()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("private async Task<StageResult> RunG3WcsCenteringAsync(", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task<StageResult> RunBoundedG3LocalSearchAsync(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var body = source[start..end];
        var overlap = body.IndexOf("var overlapMove = G3FieldOverlapPolicy.CapMove(", StringComparison.Ordinal);
        var scale = body.IndexOf("var scale = overlapMove.AppliedScale;", overlap, StringComparison.Ordinal);
        var reserve = body.IndexOf("G3AcquisitionMotionPlanner.ValidateOutboundAndReturnReserve(", scale, StringComparison.Ordinal);
        var intent = body.IndexOf("Phase = G3AcquisitionMotionPhase.OutboundIntent", reserve, StringComparison.Ordinal);
        Assert.True(overlap > 0 && scale > overlap && reserve > scale && intent > reserve);
        Assert.DoesNotContain("sparseFieldRecoveryNeeded", body[overlap..reserve]);
        Assert.DoesNotContain("!currentField.Solve.IsFormal", body[overlap..reserve]);
        Assert.Contains("state.CumulativeMotionArcseconds", body[overlap..intent]);
        Assert.Contains("state.MaximumCorrectionAttempts", body[overlap..intent]);
    }

    [Fact]
    public void RecordedHd220526FirstFormalToSevenHundredFortySevenArcsecondMoveIsCappedBeforeTheMissingFrame()
    {
        var root = Environment.GetEnvironmentVariable("UVEX_REPLAY_OBSERVATIONS");
        if (string.IsNullOrWhiteSpace(root)) return;
        var evidence = Path.Combine(root, "UVEX-20261023T124609Z-4f199a0a5f07404", "evidence");
        var raw = Assert.Single(Directory.GetFiles(evidence, "00033-*-g3-plate-solve-probe-01-5000ms.fit"));
        var failedRaw = Assert.Single(Directory.GetFiles(evidence, "00037-*-g3-plate-solve-probe-01-5000ms.fit"));
        var solvedPath = Assert.Single(Directory.GetFiles(evidence, "00034-*-plate-solve-evidence.json"));
        var movePath = Assert.Single(Directory.GetFiles(evidence, "00044-*-g3-wcs-centering-attempt.json"));
        var paths = new[] { raw, failedRaw, solvedPath, movePath };
        var before = paths.ToDictionary(p => p, Hash);
        using var solvedDoc = JsonDocument.Parse(File.ReadAllText(solvedPath));
        using var moveDoc = JsonDocument.Parse(File.ReadAllText(movePath));
        var payload = solvedDoc.RootElement.GetProperty("payload");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(Hash(raw), solvedDoc.RootElement.GetProperty("source").GetProperty("sha256").GetString()!.ToUpperInvariant());
        var solved = payload.GetProperty("solved");
        var motion = moveDoc.RootElement.GetProperty("payload");
        Assert.Equal(0, motion.GetProperty("failedNeighbourApproachesBefore").GetInt32());
        var desired = motion.GetProperty("desiredG3Center");
        var delta = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(
            solved.GetProperty("raDegrees").GetDouble(), solved.GetProperty("decDegrees").GetDouble(),
            desired.GetProperty("raDegrees").GetDouble(), desired.GetProperty("dec").GetDouble());
        var magnitude = Math.Sqrt(delta.RaArcseconds * delta.RaArcseconds + delta.DecArcseconds * delta.DecArcseconds);
        Assert.Equal(747.08104198195, magnitude, 5);
        var fits = QhyFitsCodec.Read(raw);
        var plan = G3FieldOverlapPolicy.CapMove(new(fits.Width, fits.Height,
            solved.GetProperty("pixelScaleArcseconds").GetDouble(), null, 5),
            delta.RaArcseconds, delta.DecArcseconds, 5398);
        Assert.True(plan.Overlap.Usable);
        Assert.True(plan.AppliedScale < 0.25);
        var capped = Math.Sqrt(plan.RaTangentArcseconds * plan.RaTangentArcseconds + plan.DeclinationArcseconds * plan.DeclinationArcseconds);
        Assert.InRange(capped, 186, 187);
        // This proves an unchanged-budget planning correction, not that missing
        // historical intermediate frames now exist or a new sky run succeeded.
        foreach (var path in paths) Assert.Equal(before[path], Hash(path));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static Task<StageResult?> Unexpected(CancellationToken _) =>
        throw new InvalidOperationException("A later production fallback was incorrectly entered.");

    private static StageResult OpticalMiss()
    {
        var attempts = Enumerable.Range(1, 2).Select(i =>
        {
            var path = $"synthetic-original-{i}.fits";
            var binding = new G3FieldMountBinding(1, "sparse-run", "original-action", "original-preset", path,
                "immutable-source-sha", DateTimeOffset.UtcNow, 45, 40, "J2000", "pierEast", DateTimeOffset.UtcNow, "binding-sha");
            return new G3PlateSolveAttemptEvidence(i, i * 1000, "G3_PLATE_SOLVE_TIER_FAILED",
                GateDisposition.Indeterminate, false, path, $"attempt-{i}.json", "G3_CLOUD_OR_TRANSPARENCY_INVALID", 0, binding);
        }).ToArray();
        var gate = RealObservationStageRunner.ClassifyG3EmptyLadder(attempts, 2);
        Assert.Equal(GateFailureScope.TargetQuality, gate.FailureScope);
        return new(gate, "original-ladder-evidence.json");
    }

    private static G3AcquisitionMotionState Ledger(DateTimeOffset now) => new(
        G3AcquisitionMotionState.CurrentSchemaVersion, G3AcquisitionMotionState.CurrentTangentProjectionId,
        "sparse-run", "6e7fa5f26be9409088254a91b94d9d65", new('A', 64), new('B', 64), new('C', 64),
        G3AcquisitionMotionKind.LocalSearch, G3AcquisitionMotionPhase.SettledBudgetLedger, "pierEast", "J2000",
        10, 20, 10, 20, 10, 20, 0, 0, 0, 310, 900, 3600, 12, 2, 90, 1200,
        1340, 8, now.AddMinutes(-12), now.AddMinutes(-12), now, "original-declaration.json");
}
