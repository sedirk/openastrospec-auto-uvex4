using System.Reflection;
using System.Text.Json;
using NINA.Core.Interfaces;
using NINA.Profile.Interfaces;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AutomaticSepFocusBoundaryTests
{
    private static SepMainFocusOptions Options => new()
    { MinimumPosition = 0, MaximumPosition = 10000, Step = 60, HalfPoints = 3, Frames = 5,
        ExposureMilliseconds = 5000, MaximumSeconds = 900 };
    private static FocusMotionLimits Limits => new(0, 10000, 750, 12500, FocusApproachDirection.IncreasingSteps, 300);

    [Fact]
    public async Task ApprovedConcreteScanAndSharedAbabFitConsumeOneReservationWithinOriginalMotionCaps()
    {
        var fake = new FocusFake { Position = 5275, Backlash = 300, Candidate = 5220 };
        var reserveCount = 0;
        var reservedBeforeEveryMove = true;
        var boundary = Wrap(fake, reserve: _ => { reserveCount++; return Task.FromResult(true); });
        fake.BeforeMove = () => reservedBeforeEveryMove &= reserveCount == 1;
        Assert.Equal(12240, AutomaticSepFocusBoundary.RequiredTravel(Options, await fake.ReadReadyAsync(default), Limits));
        var result = await Run(boundary);
        Assert.True(result.FocusVerified, result.Message);
        Assert.Equal(5220, result.FinalPosition);
        Assert.NotEmpty(fake.Moves);
        Assert.True(reservedBeforeEveryMove);
        Assert.Equal(1, reserveCount);
        Assert.True(boundary.ScanReserved);
        Assert.InRange(boundary.ChargedSteps, 1, Limits.MaximumCumulativeMoveSteps);
        Assert.All(fake.Moves, m => Assert.InRange(Math.Abs(m.To - m.From) + 600, 1, 750));
        Assert.Equal(fake.Moves.Sum(m => (long)Math.Abs(m.To - m.From) + 600), boundary.ChargedSteps);
    }

    [Fact]
    public async Task RefusedNightReservationLeavesOriginAndRemainsAnOrdinaryTargetQualityFailure()
    {
        var fake = new FocusFake();
        var boundary = Wrap(fake, reserve: _ => Task.FromResult(false));
        var result = await Run(boundary);
        Assert.False(boundary.ScanReserved);
        Assert.Empty(fake.Moves);
        // The shared runner pre-recorded its move intent before the reservation
        // was refused. Only the separate owner stop/origin readback can settle
        // this no-command branch; do not forge the runner's return receipt.
        Assert.False(result.ReturnConfirmed);
        Assert.Null(result.FinalPosition);
        Assert.Equal(fake.Origin, fake.Position);
        Assert.Null(boundary.BoundaryFailure);
        var outcome = AutomaticSepFocusBoundary.ClassifyOutcome(result, null, fake.Origin, fake.Position, true, false, false, null);
        Assert.Equal(GateFailureScope.TargetQuality, outcome!.FailureScope);
    }

    [Fact]
    public async Task OperatorCancellationCannotUseManualRunnersFreshReturnTokenToMove()
    {
        using var cancel = new CancellationTokenSource();
        var fake = new FocusFake();
        fake.BeforeMove = () => cancel.Cancel();
        var stops = 0;
        var boundary = Wrap(fake, token: cancel.Token, stop: _ => { stops++; return Task.FromResult<IReadOnlyList<string>>([]); });
        var result = await Run(boundary, cancel.Token);
        Assert.Single(fake.Moves);
        Assert.True(stops > 0);
        Assert.False(result.ReturnConfirmed);
        Assert.NotEqual(fake.Origin, fake.Position);
        Assert.Null(boundary.BoundaryFailure); // Cancellation is not mislabelled an owner change.
        Assert.Equal("AUTO_FOCUS_CANCELLED", AutomaticSepFocusBoundary.ClassifyOutcome(result, null,
            fake.Origin, fake.Position, true, false, true, null)!.Code);
    }

    [Fact]
    public async Task SafetyExceptionCaughtBySharedRunnerRemainsLatchedAfterPhysicalStop()
    {
        var unsafeNow = false;
        var fake = new FocusFake { OnCapture = () => unsafeNow = true };
        var boundary = Wrap(fake, safety: _ => unsafeNow ? Task.FromException(new InvalidOperationException("unsafe weather")) : Task.CompletedTask);
        var result = await Run(boundary);
        Assert.Empty(fake.Moves);
        Assert.False(result.FocusVerified);
        Assert.Equal("unsafe weather", boundary.BoundaryFailure!.Message);
        var gate = GateResult.Unknown("WEATHER_UNSAFE", boundary.BoundaryFailure.Message);
        Assert.Same(gate, AutomaticSepFocusBoundary.ClassifyOutcome(result, gate, fake.Origin, fake.Position, true, true, true, null));
    }

    [Fact]
    public async Task RealWrapperLocalDeadlineAtOriginProducesSkipNotNewNightBlock()
    {
        var fake = new FocusFake { SlowCapture = true };
        var end = DateTimeOffset.UtcNow.AddMilliseconds(120);
        var stops = 0;
        var boundary = Wrap(fake, deadline: end, stop: _ => { stops++; return Task.FromResult<IReadOnlyList<string>>([]); });
        var result = await Run(boundary);
        Assert.True(DateTimeOffset.UtcNow >= end);
        Assert.True(stops > 0);
        Assert.Empty(fake.Moves);
        Assert.False(result.FocusVerified);
        var outcome = AutomaticSepFocusBoundary.ClassifyOutcome(result, null, fake.Origin, fake.Position,
            true, true, false, null);
        Assert.Equal("AUTO_FOCUS_BUDGET_EXHAUSTED_RETURNED", outcome!.Code);
        Assert.Equal(GateFailureScope.TargetQuality, outcome.FailureScope);
    }

    [Fact]
    public async Task StopFailureIsNotConvertedToConfirmedReturnOrQuality()
    {
        var fake = new FocusFake { SlowCapture = true };
        var boundary = Wrap(fake, deadline: DateTimeOffset.UtcNow.AddMilliseconds(120),
            stop: _ => Task.FromResult<IReadOnlyList<string>>(["motor not stopped"]));
        var result = await Run(boundary);
        Assert.Contains("停止边界未确认", result.Message);
        Assert.False(result.FocusVerified);
        var outcome = AutomaticSepFocusBoundary.ClassifyOutcome(result,
            GateResult.Unknown("AUTO_FOCUS_STOP_UNCONFIRMED", "motor not stopped"), fake.Origin, fake.Position, false, true, false, null);
        Assert.NotEqual(GateFailureScope.TargetQuality, outcome!.FailureScope);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void DeadlineAfterVerifiedFitCanSkipAtLegalNewPositionButUnprovenPositionCannot(bool verified, bool positionConfirmed, bool quality)
    {
        var result = new SepMainFocusResult(verified ? "Improved" : "Failed", 5275, 5220, 5220,
            verified, verified, "test", "", [], []);
        var gate = AutomaticSepFocusBoundary.ClassifyOutcome(result, null, 5275, 5220, positionConfirmed, true, true, null);
        Assert.Equal(quality, gate!.FailureScope == GateFailureScope.TargetQuality);
        var night = GateResult.Unknown("NIGHT_DAWN_REACHED", "dawn");
        Assert.Same(night, AutomaticSepFocusBoundary.ClassifyOutcome(result, null, 5275, 5220, positionConfirmed, true, true, night));
    }

    [Fact]
    public async Task PhysicalIdleCannotSubstituteForJoiningUnderlyingNativeBacklashTask()
    {
        var underlyingNative = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var wrapper = underlyingNative.Task.WaitAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wrapper);
        // The wrapper is done and a motor may momentarily report idle, but the
        // native backlash task can still issue another segment until it joins.
        Assert.True(wrapper.IsCompleted);
        Assert.False(underlyingNative.Task.IsCompleted);
        Assert.False(await NinaSepMainFocusHardware.TryJoinOwnerOperationAsync(underlyingNative.Task, TimeSpan.FromMilliseconds(15), default));
        underlyingNative.SetResult(5275);
        Assert.True(await NinaSepMainFocusHardware.TryJoinOwnerOperationAsync(underlyingNative.Task, TimeSpan.FromMilliseconds(15), default));
        Assert.True(await NinaSepMainFocusHardware.TryJoinOwnerOperationAsync(Task.FromException(new InvalidOperationException("joined failure")),
            TimeSpan.FromMilliseconds(15), default));
    }

    [Fact]
    public async Task EntireUnjoinedCaptureOrMeasurementOperationIsLatchedEvenWhenNativeOwnersReportStopped()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FocusFake { IgnoredCaptureUntil = completion.Task };
        var boundary = Wrap(fake, deadline: DateTimeOffset.UtcNow.AddMilliseconds(120));
        _ = await boundary.ReadReadyAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.CaptureAsync("synthetic-never-written.fit", null, default));
        Assert.NotNull(boundary.UnjoinedOperation);
        Assert.False(boundary.UnjoinedOperation.IsCompleted);
        Assert.NotNull(boundary.BoundaryFailure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.ReadReadyAsync(default));
        Assert.Empty(fake.Moves);
        completion.SetResult();
        await boundary.UnjoinedOperation;
        Assert.NotNull(boundary.UnjoinedOperation); // No fresh runner/recheck can erase the failed boundary receipt.
    }

    [Theory]
    [InlineData("AUTO_FOCUS_MEASUREMENT_UNAVAILABLE", 5275, true)]
    [InlineData("AUTO_FOCUS_MEASUREMENT_UNAVAILABLE", 5220, false)]
    [InlineData("AUTO_FOCUS_OWNER_OR_STOP_CHANGED", 5275, false)]
    [InlineData("AUTO_FOCUS_OWNER_OR_DIAGNOSTIC_FAILED", 5275, false)]
    public void OnlyExplicitOptionalMeasurementFailureAtProvenOriginCanUseQualityFallback(string code, int final, bool quality)
    {
        var gate = AutomaticSepFocusBoundary.ClassifyOutcome(null, GateResult.Unknown(code, "test"),
            5275, final, true, false, false, null);
        Assert.Equal(quality, gate!.FailureScope == GateFailureScope.TargetQuality);
        Assert.NotEqual(GateFailureScope.TargetQuality, AutomaticSepFocusBoundary.ClassifyOutcome(null,
            GateResult.Unknown(code, "test"), 5275, final, false, false, false, null)!.FailureScope);
    }

    [Fact]
    public void OldCommissionedLimitsCannotBeSilentlyExpandedToAccommodateSavedBacklash()
    {
        var old = Limits with { MaximumSingleMoveSteps = 200, MaximumCumulativeMoveSteps = 1000, BacklashCompensationSteps = 50 };
        Assert.Equal(long.MaxValue, AutomaticSepFocusBoundary.RequiredTravel(Options, new(5275, "owner", 300, 0), old));
        var constrained = AutomaticSepFocusBoundary.Constrain(Options, old);
        Assert.NotNull(constrained);
        Assert.Equal(200, old.MaximumSingleMoveSteps);
        Assert.Null(AutomaticSepFocusBoundary.Constrain(Options, old with { MinimumPositionSteps = 10001 }));
    }

    [Fact]
    public void OptionalRuntimeAbsentOrCorruptIsAReadOnlyCapabilityMiss()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uvex-focus-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "runtime.json");
            Assert.Null(NinaSepMainFocusHardware.ReadOptionalRuntime(path, out var missing));
            Assert.NotNull(missing);
            File.WriteAllText(path, "{bad-json");
            Assert.Null(NinaSepMainFocusHardware.ReadOptionalRuntime(path, out var corrupt));
            Assert.NotNull(corrupt);
            Assert.Equal("{bad-json", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SavingFocusDraftChangesNextNightSnapshotButCannotMutateOrInvalidateActiveNightOptions()
    {
        var values = new Dictionary<string, object?>();
        var accessor = Proxy<IPluginOptionsAccessor>((method, arguments) =>
        {
            if (method.Name.StartsWith("GetValue", StringComparison.Ordinal))
                return values.TryGetValue((string)arguments[0]!, out var value) ? value : arguments[1];
            if (method.Name.StartsWith("SetValue", StringComparison.Ordinal))
            { values[(string)arguments[0]!] = arguments[1]; return null; }
            return null;
        });
        var settings = new UvexPluginSettings(Proxy<IProfileService>((_, _) => null), accessor)
        { SepMainFocusOptionsJson = JsonSerializer.Serialize(Options) };
        var solver = new PlateSolverRunConfiguration("primary", "blind", "primary-type", "blind-type",
            10, 10, 2, 500, true, .1, 5, 3, "", "", "", "", "", "", "", "");
        var locked = RealRunConfiguration.Capture(settings, solver).WithSequencePlan(new(2, 4, 0));
        var originalJson = locked.MainFocusOptionsJson;
        var originalHash = locked.ActionConfigurationSha256;
        settings.SepMainFocusOptionsJson = JsonSerializer.Serialize(Options with { AutomaticRecoveryEnabled = false, Step = 30 });
        var nextNight = RealRunConfiguration.Capture(settings, solver).WithSequencePlan(new(2, 4, 0));
        Assert.NotEqual(originalHash, nextNight.ActionConfigurationSha256);
        Assert.Equal(originalJson, locked.MainFocusOptionsJson);
        Assert.True(locked.MatchesCurrentProfile(settings, solver, out var compared));
        Assert.Equal(originalHash, compared);
        Assert.True(locked.MatchesCurrentProfile(settings, solver, "", out compared, out _));
        Assert.Equal(originalHash, compared);
        Assert.Equal(originalHash, nextNight.WithLockedMainFocusOptions(originalJson).ActionConfigurationSha256);
        settings.G3GainPercent++;
        Assert.False(locked.MatchesCurrentProfile(settings, solver, out _));
        Assert.False(locked.MatchesCurrentProfile(settings, solver, "", out _, out var difference));
        Assert.Contains("Gain", difference);
    }

    private static AutomaticSepFocusBoundary Wrap(FocusFake fake,
        Func<CancellationToken, Task>? safety = null,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? stop = null,
        Func<CancellationToken, Task<bool>>? reserve = null,
        DateTimeOffset? deadline = null, CancellationToken token = default) =>
        new(fake, Limits, Options, safety ?? (_ => Task.CompletedTask),
            stop ?? (_ => Task.FromResult<IReadOnlyList<string>>([])), reserve ?? (_ => Task.FromResult(true)),
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(15), token);

    private static async Task<SepMainFocusResult> Run(AutomaticSepFocusBoundary boundary, CancellationToken token = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "uvex-auto-focus-test-" + Guid.NewGuid().ToString("N"));
        try { return await new SepMainFocusRunner(boundary).RunAsync(Options, directory, null, token); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class FocusFake : ISepMainFocusHardware
    {
        public int Origin => 5275;
        public int Position = 5275, Candidate = 5220, Backlash = 300;
        public bool SlowCapture;
        public Task? IgnoredCaptureUntil;
        public Action? BeforeMove, OnCapture;
        public List<(int From, int To)> Moves { get; } = [];
        public Task<SepFocusState> ReadReadyAsync(CancellationToken token) => Task.FromResult(new SepFocusState(Position, "immutable-owner", Backlash, 0));
        public Task MoveAsync(int position, CancellationToken token)
        {
            Moves.Add((Position, position));
            BeforeMove?.Invoke();
            Position = position;
            return Task.CompletedTask;
        }
        public async Task<SepFocusFrame> CaptureAsync(string path, SepFocusReference[]? reference, CancellationToken token)
        {
            if (SlowCapture) await Task.Delay(5000, token);
            if (IgnoredCaptureUntil is { } pending) await pending.ConfigureAwait(false);
            OnCapture?.Invoke();
            reference ??= [new(100, 100), new(300, 100), new(100, 300)];
            var radius = 5 + .05 * Math.Abs(Position - Candidate);
            return new(reference, reference.Select((r, i) => new SepFocusStar(i, r.X, r.Y, radius, radius * 1.5, 10000, 100, .9, 25)).ToArray());
        }
        public Task<SepFocusFit> FitAsync(SepFocusPoint[] points, CancellationToken token) => Task.FromResult(new SepFocusFit(Candidate, .99));
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, InterfaceProxy>();
        ((InterfaceProxy)(object)proxy).Handler = handler;
        return proxy;
    }
    private class InterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => Handler(method!, arguments ?? []);
    }
}
