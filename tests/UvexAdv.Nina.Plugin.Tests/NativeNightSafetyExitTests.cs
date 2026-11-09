using NINA.Astrometry.Interfaces;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using UvexAdv.Nina.Plugin.SequenceItems;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed partial class NativeSequencePlanTests
{
    [Theory]
    [InlineData("delayed", true)] [InlineData("delayed", false)]
    [InlineData("stuck", true)] [InlineData("stuck", false)]
    [InlineData("disconnected", true)] [InlineData("identity", false)]
    public async Task WeatherDuringNativeSlewDrainsOrRetainsDiagnosticGuardian(string mode, bool holdOpen)
    {
        InitializeAstrometry();
        var settings = Settings();
        var astrometry = Proxy<IAstrometrySettings>();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? astrometry : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        using var nativeToken = new CancellationTokenSource();
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings)
        { StopAtDawn = false, HoldOpenAfterTargets = holdOpen, EndAtIso8601 = DateTimeOffset.Now.AddHours(1).ToString("o") };
        var first = new WeatherCancellationBoundaryRunner(mode, () => { night.OnNightUnsafe(); nativeToken.Cancel(); });
        var second = new TerminalGateRunner(null, false);
        for (var i = 0; i < 2; i++)
        {
            IObservationStageRunner runner = i == 0 ? first : second;
            night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings, () => runner)
            {
                TargetName = i == 0 ? "HD 18552 cancellation replay" : "Must not start in unsafe weather",
                CatalogId = $"SIM-{i}", NightSetupId = "SIM-NIGHT", UseRealMode = false,
                SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120,
                RightAscensionDegrees = 0, DeclinationDegrees = 89, DurationMinutes = 1,
                HorizonMinimumDegrees = 0, HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0,
                ExpectedAtrCameraId = "SIM-ATR", ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY",
                ScienceFrames = 1, MaximumScienceAttempts = 2,
            });
        }
        Assert.NotNull(await Record.ExceptionAsync(() => night.Execute(new Progress<ApplicationStatus>(), nativeToken.Token)
            .WaitAsync(TimeSpan.FromSeconds(12))));
        Assert.True(first.Cancelled); Assert.True(first.IdleChecks > 0); Assert.Empty(second.Stages);
        Assert.False(night.IsActive); Assert.False(night.NativeEndInstructionsReady);
        using var journal = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        var terminal = journal.RootElement.GetProperty("outcomes").EnumerateArray().Last();
        Assert.True(terminal.GetProperty("unsafeTripped").GetBoolean());
        Assert.False(terminal.GetProperty("ordinaryCancel").GetBoolean());
        Assert.False(terminal.GetProperty("physicalHardware").GetBoolean());
        if (mode == "delayed")
        {
            Assert.Null(host.SafetyStandby); // The checked simulated closeout, not a latent guardian.
            Assert.Empty(terminal.GetProperty("closeErrors").EnumerateArray());
        }
        else
        {
            var guard = Assert.IsType<NightSafetyStandby>(host.SafetyStandby);
            await guard.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(terminal.GetProperty("handedToStandby").GetBoolean());
            Assert.Equal("MonitoringOnly", guard.Snapshot.Phase);
            Assert.Equal("NIGHT_UNSAFE", guard.Snapshot.Reason);
            Assert.Equal(journal.RootElement.GetProperty("deadlineUtc").GetDateTimeOffset(), guard.Snapshot.DeadlineUtc);
            Assert.Contains("不能保证关顶", night.NightState);
            Assert.True(guard.Snapshot.IsActive);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReserveNightAsync(new object(), false, default));
            host.Dispose(); await guard.LifetimeCompletion.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private sealed class WeatherCancellationBoundaryRunner(string mode, Action cancel) : ObservationStageRunnerBase, INativeTargetBoundary
    {
        public bool Cancelled;
        public int IdleChecks;
        public void AttachNativeTarget(NativeTargetSession? session) { }
        public void RecordMeridianSegment(NativeTargetSession session, string runId) { }
        public ClockContinuationBudget CaptureClockContinuationBudget() => throw new InvalidOperationException("No clock recovery in weather cancellation");
        public Task<GateResult> RecoverMountClockAtIdleBoundaryAsync(ObservationPlan plan, CancellationToken token) =>
            throw new InvalidOperationException("No recovery during unsafe weather");
        public async Task<IReadOnlyList<string>> ConfirmSequenceIdleAsync(CancellationToken token, bool requireSettledMotion = true)
        {
            Assert.True(Cancelled); IdleChecks++;
            var result = await Phd2MountIdleWaitTests.ReplayCancelledSlew(mode, token);
            return result.Disposition == GateDisposition.Passed ? [] : new[] { result.Code + ": " + result.Message };
        }
        public override Task OnCancelledAsync(ObservationContext context, CancellationToken token)
        { Cancelled = true; return Task.CompletedTask; }
        public override Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken token)
        {
            if (stage == ObservationStage.AcquireG3SlitField) { cancel(); token.ThrowIfCancellationRequested(); }
            return Task.FromResult(new StageResult(GateResult.Pass("SIM_STAGE", "Recorded cancellation timing; no hardware")));
        }
    }
}
