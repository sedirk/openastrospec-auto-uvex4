using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private bool independentParkAttempted;
    private string? independentParkIssue;

    // The native night closer is a new runner. Read the immutable hashed package,
    // not live Profile hints or the previous runner's mutable commissioning cache.
    private async Task<string?> ValidateIndependentRoofAuthorityAsync(CancellationToken token)
    {
        if (!configuration.Environment.RoofNeverInterferesWithMount) return "Non-interfering roof mode is not enabled.";
        if (configuration.Environment.WeakSupervisionEnabled || !configuration.Environment.RequireSafetyMonitor ||
            (!configuration.Environment.RequireWeatherData && !configuration.Environment.SafetyMonitorOwnsWeather) ||
            !configuration.Environment.RequireOpenDomeOrRoof)
            return "Non-interfering roof mode requires the full unattended safety chain.";
        try
        {
            var bytes = await File.ReadAllBytesAsync(configuration.Commissioning.PresetPath, token).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)),
                    configuration.Commissioning.PresetSha256, StringComparison.OrdinalIgnoreCase))
                return "Roof clearance package SHA-256 changed; no roof exception is authorized.";
            var preset = JsonSerializer.Deserialize<RealCommissioningPreset>(bytes,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var issues = RoofClearancePolicy.Validate(true, preset?.Environment.RoofClearance,
                configuration.ExpectedTelescopeId, configuration.Environment.Devices.DomeOrRoofId, DateTimeOffset.UtcNow);
            return issues.Count == 0 ? null : string.Join("; ", issues);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return $"Roof clearance package unavailable: {ex.Message}"; }
    }

    private bool IndependentRoofMountStopped()
    {
        var mount = telescopeMediator.GetInfo();
        return RoofClearancePolicy.IsStopped(mount.Connected,
            EnvironmentAdapterIdentityMatches(mount.DeviceId, configuration.ExpectedTelescopeId),
            mount.Slewing, mount.IsPulseGuiding, mount.TrackingEnabled);
    }

    private async Task<bool> IndependentRoofGuiderIdleAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        if (!phd2.IsConnected) await phd2.ConnectAsync(timeout.Token).ConfigureAwait(false);
        return await phd2.GetAppStateAsync(timeout.Token).ConfigureAwait(false) is
            Phd2AppState.Stopped or Phd2AppState.Selected;
    }

    // Opening preparation is not emergency cleanup: never stop an operator's
    // slew/pulse or start Park/Home here. Only disable residual sidereal tracking,
    // once, after the caller has checked the bound roof, safety and idle guider.
    internal static async Task<GateResult> PrepareMountForIndependentRoofOpenAsync(
        ITelescopeMediator mediator, string expectedId, TimeSpan limit,
        TimeSpan pollInterval, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit);
        var requested = false;
        var stable = 0;
        var last = mediator.GetInfo();
        Dictionary<string, double> Metrics() => new()
        {
            ["connected"] = last.Connected ? 1 : 0,
            ["identityMatched"] = EnvironmentAdapterIdentityMatches(last.DeviceId, expectedId) ? 1 : 0,
            ["slewing"] = last.Slewing ? 1 : 0,
            ["pulseGuiding"] = last.IsPulseGuiding ? 1 : 0,
            ["trackingEnabled"] = last.TrackingEnabled ? 1 : 0,
            ["atPark"] = last.AtPark ? 1 : 0,
            ["trackingStopRequested"] = requested ? 1 : 0,
            ["stableReadbacks"] = stable,
        };
        try
        {
            while (stable < 2)
            {
                timeout.Token.ThrowIfCancellationRequested();
                last = mediator.GetInfo();
                if (!last.Connected || !EnvironmentAdapterIdentityMatches(last.DeviceId, expectedId))
                    return GateResult.Unknown("ROOF_OPEN_MOUNT_IDENTITY_INVALID",
                        "Roof not opened: the bound mount connection or identity is not confirmed.", Metrics());
                if (last.Slewing || last.IsPulseGuiding)
                    return GateResult.Unknown("ROOF_OPEN_MOUNT_MOVING",
                        "Roof not opened: mount slew or guide pulses are active. No automatic slew stop, Home or Park was issued by opening preparation.", Metrics());
                if (last.TrackingEnabled && !requested)
                {
                    if (!last.CanSetTrackingEnabled)
                        return GateResult.Unknown("ROOF_OPEN_TRACKING_STOP_UNSUPPORTED",
                            "Roof not opened: tracking is on but the mount does not report tracking-control capability.", Metrics());
                    requested = true;
                    // N.I.N.A. returns the resulting tracking state, not a
                    // success flag. False is expected; only readback can pass.
                    await Task.Run(() =>
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        var current = mediator.GetInfo();
                        if (!current.Connected || !EnvironmentAdapterIdentityMatches(current.DeviceId, expectedId) ||
                            current.Slewing || current.IsPulseGuiding)
                            throw new InvalidOperationException("Mount identity or stopped state changed before disabling tracking.");
                        _ = mediator.SetTrackingEnabled(false);
                    }, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                    stable = 0;
                }
                else
                {
                    stable = last.TrackingEnabled ? 0 : stable + 1;
                }
                if (stable < 2) await Task.Delay(pollInterval, timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            return GateResult.Pass("ROOF_OPEN_MOUNT_PREPARED",
                "Bound mount stopped, no guide pulses, tracking off in two readbacks; no Home, Park or slew issued.", Metrics());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return GateResult.Unknown("ROOF_OPEN_TRACKING_STOP_TIMEOUT",
                "Roof not opened: tracking-off/stopped readback was not confirmed within the opening preparation timeout; no retry was issued.", Metrics());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return GateResult.Unknown("ROOF_OPEN_MOUNT_PREPARATION_FAILED",
                $"Roof not opened: mount preparation failed: {ex.Message}", Metrics());
        }
    }

    private async Task<string?> StopForIndependentRoofAsync(CancellationToken token)
    {
        var mount = telescopeMediator.GetInfo();
        if (!mount.Connected || !EnvironmentAdapterIdentityMatches(mount.DeviceId, configuration.ExpectedTelescopeId))
            return "Mount connection/identity is unknown; roof motion not started.";
        var camera = cameraMediator.GetInfo();
        if (!camera.Connected || camera.IsExposing || activeQhyJobs.Count != 0 || pendingQhyRequests.Count != 0)
            return "Acquisition owners have not confirmed idle; roof motion not started.";
        using var stopTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        stopTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            // Connect only the RPC event client, not the PHD2 physical devices.
            if (!phd2.IsConnected) await phd2.ConnectAsync(stopTimeout.Token).ConfigureAwait(false);
            await StopPhdAndWaitAsync(stopTimeout.Token).ConfigureAwait(false);
            await StopMountForIndependentRoofAsync(telescopeMediator,
                configuration.ExpectedTelescopeId, stopTimeout.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return "Mount/guiding stop was not confirmed within 20 seconds; roof motion not started."; }
    }

    internal static async Task StopMountForIndependentRoofAsync(ITelescopeMediator mediator,
        string expectedId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var mount = mediator.GetInfo();
        if (!mount.Connected || !EnvironmentAdapterIdentityMatches(mount.DeviceId, expectedId))
            throw new InvalidOperationException("Mount connection/identity changed before stop; roof motion not started.");
        if (mount.Slewing || mount.IsPulseGuiding) mediator.StopSlew();
        // N.I.N.A. 3.2 returns the resulting TrackingEnabled value, NOT success.
        // false is the desired result. Neither return value replaces readback.
        if (mount.TrackingEnabled) _ = mediator.SetTrackingEnabled(false);
        var stable = 0;
        while (stable < 2)
        {
            token.ThrowIfCancellationRequested();
            mount = mediator.GetInfo();
            if (!mount.Connected || !EnvironmentAdapterIdentityMatches(mount.DeviceId, expectedId))
                throw new InvalidOperationException("Mount connection/identity changed during stop; roof motion not started.");
            stable = RoofClearancePolicy.IsStopped(mount.Connected, true,
                mount.Slewing, mount.IsPulseGuiding, mount.TrackingEnabled) ? stable + 1 : 0;
            await Task.Delay(250, token).ConfigureAwait(false);
        }
    }

    private async Task<string?> CloseIndependentRoofAsync(string reason, CancellationToken token)
    {
        if (await ValidateIndependentRoofAuthorityAsync(token).ConfigureAwait(false) is { } authorityIssue)
            return authorityIssue;
        return await RoofClearancePolicy.CloseThenParkAsync(StopForIndependentRoofAsync, async closeToken =>
        {
            var selection = ValidateCurrentEnvironmentDeviceSelections();
            if (selection.Disposition != GateDisposition.Passed) return selection.Message;
            var roof = domeMediator.GetInfo();
            if (!IndependentRoofMountStopped() || !roof.Connected ||
                !EnvironmentAdapterIdentityMatches(roof.DeviceId, configuration.Environment.Devices.DomeOrRoofId))
                return "Stopped mount or locked roof identity changed; no close command was sent.";
            if (roof.ShutterStatus != ShutterState.ShutterClosed)
            {
                if (!roof.CanSetShutter || roof.ShutterStatus == ShutterState.ShutterError)
                    return "Roof cannot accept a close command or reports ShutterError.";
                Report("无干涉屋顶：采集与运动已停止，先关顶；停放单独处理");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closeToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(configuration.Environment.DomeOrRoofTransitionTimeoutSeconds));
                if (!await domeMediator.CloseShutter(timeout.Token).ConfigureAwait(false))
                    return "N.I.N.A./RRCI rejected roof closure; controller interlocks were not bypassed.";
                var closed = await WaitForDomeOrRoofStateAsync(ShutterState.ShutterClosed, timeout.Token).ConfigureAwait(false);
                if (closed.Disposition != GateDisposition.Passed) return $"{closed.Code}: {closed.Message}";
            }
            Interlocked.Exchange(ref domeOrRoofOpenEstablished, 0);
            await WriteAuditBestEffortAsync("unattended-roof-close", new
            {
                reason, mode = "NonInterfering", roofClosed = true,
                mountParked = telescopeMediator.GetInfo().AtPark,
                parkNotRequiredForRoof = true, configuration.Commissioning.PresetSha256,
            }).ConfigureAwait(false);
            return null;
        }, ParkAfterIndependentRoofAsync, token).ConfigureAwait(false);
    }

    private async Task<string?> ParkAfterIndependentRoofAsync(CancellationToken token)
    {
        if (independentParkAttempted) return independentParkIssue;
        independentParkAttempted = true;
        // Store the pending result before issuing motion; repeated cleanup cannot retry Park.
        independentParkIssue = "Roof closed; independent park has not been confirmed.";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(configuration.Environment.DomeOrRoofTransitionTimeoutSeconds +
            (configuration.SequencePlan?.HomeBeforeParkOnFinalize == true ? configuration.SequencePlan.MountHomeTimeoutSeconds : 0)));
        try
        {
            if (!IndependentRoofMountStopped()) return independentParkIssue = "Roof closed; mount identity/stopped state changed before independent Park.";
            var mount = telescopeMediator.GetInfo();
            if (mount.AtPark) return independentParkIssue = null;
            if (!mount.CanPark) return independentParkIssue = "Roof closed; mount does not support Park.";
            if (configuration.SequencePlan?.HomeBeforeParkOnFinalize == true)
            {
                var roof = domeMediator.GetInfo();
                if (!roof.Connected || roof.ShutterStatus != ShutterState.ShutterClosed ||
                    !EnvironmentAdapterIdentityMatches(roof.DeviceId, configuration.Environment.Devices.DomeOrRoofId) ||
                    !await IndependentRoofGuiderIdleAsync(timeout.Token).ConfigureAwait(false) ||
                    !cameraMediator.GetInfo().Connected || cameraMediator.GetInfo().IsExposing)
                    return independentParkIssue = "Roof closed/readback or idle owners changed; Home and Park not started.";
                Report("屋顶已关：先有界 Home 回零，核对到位后再 Park；每项最多一次");
                var home = await HomeAndVerifyMountAsync(telescopeMediator, configuration.ExpectedTelescopeId,
                    progress, TimeSpan.FromSeconds(configuration.SequencePlan.MountHomeTimeoutSeconds),
                    TimeSpan.FromMilliseconds(250), timeout.Token).ConfigureAwait(false);
                await WriteAuditBestEffortAsync("native-sequence-mount-home", home).ConfigureAwait(false);
                if (home.Disposition != GateDisposition.Passed)
                    return independentParkIssue = $"Roof closed; {home.Code}: {home.Message}; Park not started.";
            }
            // Home and Park have independent bounded waits; Home never consumes
            // or extends the configured Park allowance.
            timeout.CancelAfter(TimeSpan.FromSeconds(configuration.Environment.DomeOrRoofTransitionTimeoutSeconds));
            if (!IndependentRoofMountStopped())
                return independentParkIssue = "Roof closed; mount identity/stopped state changed before Park.";
            Report("屋顶已确认关闭；独立尝试停放赤道仪，失败不会重开屋顶");
            if (!await telescopeMediator.ParkTelescope(progress, timeout.Token).ConfigureAwait(false))
                return independentParkIssue = "Roof closed; N.I.N.A. did not complete independent Park.";
            var parked = await WaitForMountParkedAsync(timeout.Token).ConfigureAwait(false);
            return independentParkIssue = parked.Disposition == GateDisposition.Passed ? null : $"Roof closed; {parked.Code}: {parked.Message}";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return independentParkIssue = "Roof closed; independent Park exceeded its bounded timeout."; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return independentParkIssue = $"Roof closed; independent Park failed: {ex.Message}"; }
        finally
        {
            if (independentParkIssue is not null)
            {
                try { telescopeMediator.StopSlew(); }
                catch (Exception ex) { independentParkIssue += $" Stop request failed: {ex.Message}"; }
            }
            await WriteAuditBestEffortAsync("independent-roof-mount-park", new
            {
                error = independentParkIssue, parked = telescopeMediator.GetInfo().AtPark,
                homeBeforeParkRequested = configuration.SequencePlan?.HomeBeforeParkOnFinalize == true,
                slewing = telescopeMediator.GetInfo().Slewing, roof = domeMediator.GetInfo().ShutterStatus.ToString(),
            }).ConfigureAwait(false);
        }
    }
}
