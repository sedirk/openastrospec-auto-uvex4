using System.Text.Json;
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
    [InlineData(true)] [InlineData(false)]
    public async Task NightStartHomeIsSharedAcrossRealNativeChildrenAfterHorizonAndQualitySkip(bool enabled)
    {
        InitializeAstrometry();
        var settings = Settings();
        var profile = Proxy<IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? Proxy<IAstrometrySettings>() : Default(m.ReturnType));
        var profiles = Proxy<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : Default(m.ReturnType));
        using var host = new ObservationCoordinatorHost(new SilentNotifier());
        var night = new UvexNightSequenceContainer(profiles, host, null!, settings) { StopAtDawn = false, HomeMountAtNightStart = enabled };
        var jd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 86400000d + 2440587.5;
        var ra = ((280.46061837 + 360.98564736629 * (jd - 2451545) + 120) % 360 + 360) % 360;
        var events = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            night.Add(new UvexTargetObservationContainer(profiles, Proxy<INighttimeCalculator>(), host, null!, settings,
                () => new StartHomePort(index, enabled, events))
            {
                TargetName = $"test {i}", CatalogId = $"SIM-{i}", NightSetupId = "SIM-NIGHT", UseRealMode = false,
                SiteLatitudeDegrees = 33, SiteLongitudeDegreesEast = 120, RightAscensionDegrees = i == 0 ? (ra + 180) % 360 : ra,
                DeclinationDegrees = i == 0 ? -33 : 33, DurationMinutes = 1, HorizonMinimumDegrees = 0,
                HorizonStartMarginDegrees = 0, HorizonContinueMarginDegrees = 0, ExpectedAtrCameraId = "SIM-ATR",
                ExpectedG3ProfileName = "SIM-G3", ExpectedQhyCameraId = "SIM-QHY", ScienceFrames = 1, MaximumScienceAttempts = 2,
                FailurePolicy = SpectroscopyTargetFailurePolicy.SkipQualityFailure
            });
        }
        await night.Execute(new Progress<ApplicationStatus>(), default).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(enabled ? new[] { "home", "goto:1", "goto:2", "science:2" } :
            new[] { "goto:1", "goto:2", "science:2" }, events);
        using var j = JsonDocument.Parse(await File.ReadAllTextAsync(night.JournalPath));
        Assert.Equal(new[] { "SkippedPreflightHorizon", "SkippedQualityFailure", "Completed" },
            j.RootElement.GetProperty("outcomes").EnumerateArray().Where(o => o.TryGetProperty("outcome", out _))
                .Select(o => o.GetProperty("outcome").GetString()));
        Assert.False(j.RootElement.GetProperty("physicalHardware").GetBoolean());
        if (enabled)
        {
            var repeated = await Assert.ThrowsAsync<InvalidOperationException>(() => night.Execute(new Progress<ApplicationStatus>(), default));
            Assert.Contains("NIGHT_START_HOME_REENTRY", repeated.Message);
            Assert.Single(events, e => e == "home");
            night.ResetProgress();
            Assert.Null(night.StartHome);
            await night.Execute(new Progress<ApplicationStatus>(), default).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, events.Count(e => e == "home"));
        }
    }

    private sealed class StartHomePort(int index, bool enabled, List<string> events) : ObservationStageRunnerBase, INativeTargetBoundary
    {
        private NativeTargetSession? session;
        public void AttachNativeTarget(NativeTargetSession? s) { session = s; Assert.Equal(enabled, s!.Original.HomeMountAtNightStart); }
        public override async Task<StageResult> ExecuteStageAsync(ObservationStage stage, ObservationContext context, CancellationToken ct)
        {
            if (stage == ObservationStage.SlewToCatalogTarget)
            {
                if (session!.StartHome is { } receipt)
                {
                    var home = await receipt.EnsureAsync(context.Plan.ObservationRunId, "sim-mount", "sim-plan",
                        _ => Task.FromResult(GateResult.Pass("SIM_BOUNDARY", "port")), _ =>
                        { events.Add("home"); return Task.FromResult(GateResult.Pass("NIGHT_HOME_VERIFIED", "simulated owner readback")); }, ct);
                    if (home.Disposition != GateDisposition.Passed) return new(home);
                }
                events.Add($"goto:{index}");
            }
            if (stage == ObservationStage.AcquireG3SlitField && index == 1)
                return new(GateResult.TargetQuality("G3_SEARCH_NOT_STARTED_RETURNED", "synthetic quality skip after the first eligible native child"));
            if (stage == ObservationStage.RunScienceBlock) events.Add($"science:{index}");
            return new(GateResult.Pass("SIM_STAGE", "No real acquisition"));
        }
        public void RecordMeridianSegment(NativeTargetSession s, string runId) => s.RecordSegment(runId, index == 2 ? 1 : 0, index == 2 ? 1 : 0, 0, 0);
        public ClockContinuationBudget CaptureClockContinuationBudget() => throw new InvalidOperationException("No optical recovery expected");
        public Task<IReadOnlyList<string>> ConfirmSequenceIdleAsync(CancellationToken ct, bool requireSettledMotion = true) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<GateResult> RecoverMountClockAtIdleBoundaryAsync(ObservationPlan failedPlan, CancellationToken ct) =>
            throw new InvalidOperationException("No clock recovery expected");
    }
}
