using System.Text.Json;
using System.Reflection;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class RoofClearancePolicyTests
{
    [Fact]
    public void DriverChangeIsExplainedAndRequiresFreshOwnerBoundEvidence()
    {
        var evidence = new RoofClearanceAttestation(1, "previous", "owner", Now.AddDays(-1),
            "mount-a", "RRCIAdvanced.Dome", true, "Original owner-confirmed clearance");
        var error = Assert.Single(RoofClearancePolicy.Validate(true, evidence, "mount-a", "RRCI.Dome", Now));
        Assert.Contains("RRCIAdvanced.Dome", error);
        Assert.Contains("RRCI.Dome", error);
        Assert.Contains("操作员确认", error);
        var renewed = evidence with { EvidenceId = "new-owner-confirmation", RoofDeviceId = "RRCI.Dome", ConfirmedUtc = Now };
        Assert.Empty(RoofClearancePolicy.Validate(true, renewed, "mount-a", "RRCI.Dome", Now));
        Assert.NotEmpty(RoofClearancePolicy.Validate(true, renewed, "other-mount", "RRCI.Dome", Now));
        Assert.NotEmpty(RoofClearancePolicy.Validate(true, renewed with { AllMountPositionsClear = false }, "mount-a", "RRCI.Dome", Now));
    }

    [Theory]
    [InlineData("off", true)] [InlineData("already-off", true)] [InlineData("delayed", true)]
    [InlineData("true-ack-off", true)] [InlineData("false-ack-on", false)] [InlineData("stays-on", false)]
    [InlineData("slewing", false)] [InlineData("pulse", false)] [InlineData("unsupported", false)]
    [InlineData("disconnected", false)] [InlineData("identity", false)]
    [InlineData("identity-changed", false)] [InlineData("disconnect-after", false)]
    [InlineData("slew-after", false)] [InlineData("pulse-after", false)]
    public async Task OpeningPreparesOnlyTrackingAndRequiresTwoStoppedReadbacks(string mode, bool pass)
    {
        var info = new TelescopeInfo { Connected = mode != "disconnected", DeviceId = mode == "identity" ? "other" : "mount-a",
            TrackingEnabled = mode != "already-off", CanSetTrackingEnabled = mode != "unsupported",
            Slewing = mode == "slewing", IsPulseGuiding = mode == "pulse" };
        var calls = new List<string>();
        var requests = 0;
        var reads = 0;
        var mediator = DispatchProxy.Create<ITelescopeMediator, TelescopeProxy>();
        ((TelescopeProxy)(object)mediator).Handler = (method, args) =>
        {
            calls.Add(method.Name);
            if (method.Name == "SetTrackingEnabled")
            {
                Assert.False((bool)args![0]!);
                requests++;
                if (mode is "off" or "true-ack-off") info.TrackingEnabled = false;
                return mode is not ("off" or "false-ack-on");
            }
            if (method.Name == "GetInfo")
            {
                if (requests > 0 && ++reads >= 2)
                {
                    if (mode == "delayed") info.TrackingEnabled = false;
                    if (mode == "identity-changed") info.DeviceId = "other";
                    if (mode == "disconnect-after") info.Connected = false;
                    if (mode == "slew-after") info.Slewing = true;
                    if (mode == "pulse-after") info.IsPulseGuiding = true;
                }
                return info;
            }
            throw new InvalidOperationException("Unexpected mount action");
        };
        var result = await RealObservationStageRunner.PrepareMountForIndependentRoofOpenAsync(mediator,
            "mount-a", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), CancellationToken.None);
        Assert.Equal(pass, result.Disposition == GateDisposition.Passed);
        Assert.All(calls, c => Assert.Contains(c, new[] { "GetInfo", "SetTrackingEnabled" }));
        Assert.InRange(requests, 0, 1);
        if (mode is "slewing" or "pulse" or "identity" or "disconnected" or "unsupported" or "already-off")
            Assert.Equal(0, requests);
        if (pass) Assert.Equal(2, result.Metrics!["stableReadbacks"]);
        if (mode is "stays-on" or "false-ack-on") Assert.Equal("ROOF_OPEN_TRACKING_STOP_TIMEOUT", result.Code);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OpeningCancellationNeverRetriesOrEnablesTracking(bool cancelBefore)
    {
        using var cancel = new CancellationTokenSource();
        var info = new TelescopeInfo { Connected = true, DeviceId = "mount-a", TrackingEnabled = true, CanSetTrackingEnabled = true };
        var requests = 0;
        var mediator = DispatchProxy.Create<ITelescopeMediator, TelescopeProxy>();
        ((TelescopeProxy)(object)mediator).Handler = (method, args) =>
        {
            if (method.Name == "GetInfo") return info;
            if (method.Name == "SetTrackingEnabled")
            {
                Assert.False((bool)args![0]!); requests++; cancel.Cancel(); return false;
            }
            throw new InvalidOperationException("Unexpected mount action");
        };
        if (cancelBefore) cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RealObservationStageRunner.PrepareMountForIndependentRoofOpenAsync(
            mediator, "mount-a", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), cancel.Token));
        Assert.Equal(cancelBefore ? 0 : 1, requests);
    }

    [Fact]
    public void OpeningFailureExplainsMountPrerequisiteInsteadOfOnlyClosedRoof()
    {
        var presentation = ObservationUiPresentation.Present(ObservationStage.ValidateNightSetup,
            GateResult.Unknown("ROOF_OPEN_MOUNT_MOVING", "Mount stopped state not confirmed."),
            System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
        Assert.Contains("尚未发出开顶命令", presentation.Summary);
        Assert.Contains("重新载入", presentation.Recommendation);
    }

    [Theory]
    [InlineData("returns-off")]
    [InlineData("delayed-off")]
    [InlineData("stays-on")]
    [InlineData("identity-changed")]
    [InlineData("disconnected")]
    [InlineData("already-off")]
    public async Task NinaTrackingSetterReturnsStateAndOnlyStableReadbackAuthorizesClosure(string mode)
    {
        var info = new TelescopeInfo { Connected = true, DeviceId = "mount-a", TrackingEnabled = mode != "already-off" };
        var calls = 0;
        var readsAfterCommand = 0;
        var mediator = DispatchProxy.Create<ITelescopeMediator, TelescopeProxy>();
        ((TelescopeProxy)(object)mediator).Handler = (method, args) =>
        {
            if (method.Name == "SetTrackingEnabled")
            {
                Assert.False((bool)args![0]!);
                calls++;
                if (mode == "returns-off") info.TrackingEnabled = false;
                return mode != "returns-off"; // Exact N.I.N.A. state-return semantics.
            }
            if (method.Name == "GetInfo")
            {
                if (calls > 0 && ++readsAfterCommand >= 2)
                {
                    if (mode == "delayed-off") info.TrackingEnabled = false;
                    if (mode == "identity-changed") info.DeviceId = "different-mount";
                    if (mode == "disconnected") info.Connected = false;
                }
                return info;
            }
            throw new InvalidOperationException($"Unexpected device command: {method.Name}");
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(mode == "stays-on" ? 1 : 3));
        var operation = RealObservationStageRunner.StopMountForIndependentRoofAsync(mediator, "mount-a", timeout.Token);
        if (mode == "stays-on") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else if (mode is "identity-changed" or "disconnected") await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        else await operation;
        Assert.Equal(mode == "already-off" ? 0 : 1, calls);
    }

    private class TelescopeProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-09T11:30:00Z");
    private static RoofClearanceAttestation Evidence => new(1, "owner-clearance-v1", "station owner",
        Now, "mount-a", "roof-a", true, "Owner confirms all mount positions clear the roof.");

    [Fact]
    public void OldPackagesRetainParkRequirementAndCannotEnableException()
    {
        Assert.Empty(RoofClearancePolicy.Validate(false, null, "mount-a", "roof-a", Now));
        Assert.NotEmpty(RoofClearancePolicy.Validate(true, null, "mount-a", "roof-a", Now));
        var preset = JsonSerializer.Deserialize<EnvironmentPreset>("""
            {"RequireSafetyMonitor":true,"RequireOpenDomeOrRoof":true,"RequireWeatherData":true,
             "MaximumCloudCoverPercent":40,"MaximumHumidityPercent":95,"MaximumWindSpeedMetersPerSecond":10}
            """);
        Assert.Null(preset!.RoofClearance);
    }

    [Theory]
    [InlineData("valid", true)] [InlineData("roof", false)] [InlineData("mount", false)]
    [InlineData("time", false)] [InlineData("missing-time", false)] [InlineData("schema", false)]
    [InlineData("partial", false)] [InlineData("owner", false)] [InlineData("id", false)]
    [InlineData("basis", false)]
    public void OnlyExplicitCurrentStationAttestationIsAccepted(string change, bool accepted)
    {
        var evidence = change switch
        {
            "roof" => Evidence with { RoofDeviceId = "another-roof" },
            "mount" => Evidence with { TelescopeDeviceId = "another-mount" },
            "time" => Evidence with { ConfirmedUtc = Now.AddSeconds(1) },
            "missing-time" => Evidence with { ConfirmedUtc = default },
            "schema" => Evidence with { SchemaVersion = 9 },
            "partial" => Evidence with { AllMountPositionsClear = false },
            "owner" => Evidence with { ConfirmedBy = "" },
            "id" => Evidence with { EvidenceId = "" },
            "basis" => Evidence with { Basis = "" },
            _ => Evidence,
        };
        Assert.Equal(accepted, RoofClearancePolicy.Validate(true, evidence, "mount-a", "roof-a", Now).Count == 0);
        Assert.NotEmpty(RoofClearancePolicy.Validate(false, evidence, "mount-a", "roof-a", Now));
    }

    [Theory]
    [InlineData(true, true, false, false, false, true)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(true, true, true, false, false, false)]
    [InlineData(true, true, false, true, false, false)]
    [InlineData(true, true, false, false, true, false)]
    public void NoParkRequirementDoesNotRemoveStoppedIdentityRequirement(bool connected, bool matches,
        bool slewing, bool pulse, bool tracking, bool allowed) =>
        Assert.Equal(allowed, RoofClearancePolicy.IsStopped(connected, matches, slewing, pulse, tracking));

    [Theory]
    [InlineData("success")] [InlineData("park-false")] [InlineData("park-exception")]
    [InlineData("park-timeout")] [InlineData("stop-failed")] [InlineData("close-failed")]
    public async Task ClosureCompletesBeforeIndependentParkAndFailuresAreNotSuccess(string mode)
    {
        var calls = new List<string>();
        var result = await RoofClearancePolicy.CloseThenParkAsync(
            _ => { calls.Add("stop"); return Task.FromResult<string?>(mode == "stop-failed" ? "stop unconfirmed" : null); },
            _ => { calls.Add("closed"); return Task.FromResult<string?>(mode == "close-failed" ? "closure unconfirmed" : null); },
            _ =>
            {
                Assert.Equal(new[] { "stop", "closed" }, calls);
                calls.Add("park");
                if (mode == "park-exception") throw new InvalidOperationException("ASCOM Park rejected");
                if (mode == "park-timeout") throw new OperationCanceledException("bounded Park timeout");
                return Task.FromResult<string?>(mode == "park-false" ? "Roof closed; Park unconfirmed" : null);
            }, CancellationToken.None);
        Assert.Equal(mode == "success", result is null);
        Assert.Equal(mode == "stop-failed" ? new[] { "stop" } : mode == "close-failed"
            ? new[] { "stop", "closed" } : new[] { "stop", "closed", "park" }, calls);
    }

    [Theory]
    [InlineData("before")] [InlineData("after-stop")] [InlineData("after-close")]
    public async Task CancellationNeverStartsTheNextMechanicalAction(string boundary)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        if (boundary == "before") cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RoofClearancePolicy.CloseThenParkAsync(
            _ => { calls.Add("stop"); if (boundary == "after-stop") cancellation.Cancel(); return Task.FromResult<string?>(null); },
            _ => { calls.Add("close"); cancellation.Cancel(); return Task.FromResult<string?>(null); },
            _ => { calls.Add("park"); return Task.FromResult<string?>(null); }, cancellation.Token));
        Assert.DoesNotContain("park", calls);
        if (boundary == "before") Assert.Empty(calls);
        if (boundary == "after-stop") Assert.DoesNotContain("close", calls);
    }

    [Fact]
    public void BothProductionEntrypointsUseSharedBoundRoofPolicyAndNoUiBypass()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UVEX-ADV.sln"))) dir = dir.Parent;
        var root = Path.Combine(dir!.FullName, "src", "UvexAdv.Nina.Plugin");
        var runner = File.ReadAllText(Path.Combine(root, "RealObservationStageRunner.cs"));
        var policy = File.ReadAllText(Path.Combine(root, "RealObservationStageRunner.RoofClearance.cs"));
        var night = File.ReadAllText(Path.Combine(root, "RealObservationStageRunner.NativeSequence.cs"));
        var dock = File.ReadAllText(Path.Combine(root, "ObservationDockable.cs"));
        Assert.Contains("else if (!telescope.AtPark)", runner);
        Assert.Contains("ValidateIndependentRoofAuthorityAsync(cancellationToken)", runner);
        Assert.Contains("IndependentRoofGuiderIdleAsync", runner);
        var opening = runner[runner.IndexOf("private async Task<GateResult> EnsureDomeOrRoofOpenForUnattendedAsync(", StringComparison.Ordinal)..];
        var preparation = opening.IndexOf("PrepareMountForIndependentRoofOpenAsync", StringComparison.Ordinal);
        var prerequisite = opening.IndexOf("prerequisite = ValidateRoofOpeningPrerequisites", preparation, StringComparison.Ordinal);
        var guiderRecheck = opening.IndexOf("IndependentRoofGuiderIdleAsync", preparation, StringComparison.Ordinal);
        var roofCommand = opening.IndexOf("domeMediator.OpenShutter", preparation, StringComparison.Ordinal);
        Assert.True(preparation >= 0 && guiderRecheck > preparation && prerequisite > guiderRecheck && roofCommand > prerequisite);
        Assert.Contains("prepare.Disposition != GateDisposition.Passed", opening[preparation..roofCommand]);
        Assert.Contains("unattended-roof-open-preparation", opening[preparation..roofCommand]);
        Assert.Contains("SHA256.HashData(bytes)", policy);
        Assert.Contains("configuration.Commissioning.PresetSha256", policy);
        Assert.Contains("RoofClearancePolicy.CloseThenParkAsync", policy);
        Assert.Contains("configuration.SequencePlan?.HomeBeforeParkOnFinalize == true", policy);
        var home = policy.IndexOf("var home = await HomeAndVerifyMountAsync", StringComparison.Ordinal);
        var park = policy.IndexOf("await telescopeMediator.ParkTelescope", StringComparison.Ordinal);
        Assert.True(home > 0 && park > home);
        Assert.Contains("home.Disposition != GateDisposition.Passed", policy[home..park]);
        Assert.Contains("Park not started", policy[home..park]);
        Assert.Contains("ParkMountAndCloseDomeOrRoofAsync", night);
        Assert.DoesNotContain("CloseShutter(", night);
        Assert.DoesNotContain("CloseShutter(", dock);
        Assert.Contains("settings.RoofNeverInterferesWithMount = false", dock);
        Assert.Contains("RoofClearanceSummary", File.ReadAllText(Path.Combine(root, "Templates.xaml")));
    }
}
