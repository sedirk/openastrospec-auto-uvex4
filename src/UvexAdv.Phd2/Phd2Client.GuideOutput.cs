namespace UvexAdv.Phd2;

public sealed record Phd2EquipmentReconnectResult(
    Phd2Profile Profile, Phd2Equipment Before, Phd2Equipment After,
    long ConnectionEpoch, bool ConfirmedStopped,
    bool CaptureStarted, bool MotionCommandIssued, string? DisconnectDiagnostic);

public sealed partial class Phd2Client
{
    public void ThrowIfGuideOutputFailed()
    {
        if (Snapshot.GuideOutput is { Failed: true } status)
            throw new Phd2GuideOutputException(status);
    }

    /// <summary>
    /// One native DisconnectAll/ConnectAll at a coordinator-checked idle
    /// boundary after a latched output failure. No profile edit, camera-owner
    /// change, exposure, guide, calibration or pulse command is issued here.
    /// The coordinator must independently retain/reconcile any motion ledger.
    /// </summary>
    public async Task<Phd2EquipmentReconnectResult> ReconnectEquipmentAfterOutputFailureAsync(
        Phd2IdentityRequirement expected, CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAutomationPaused();
            if (Snapshot.GuideOutput?.Failed != true || Snapshot.Phd2Paused ||
                await GetAppStateAsync(cancellationToken).ConfigureAwait(false) != Phd2AppState.Stopped)
                throw new Phd2Exception("PHD2_OUTPUT_RECONNECT_NOT_IDLE: A latched output fault and a confirmed idle, non-paused owner are required.");
            var identity = await ValidateIdentityAsync(expected, cancellationToken).ConfigureAwait(false);
            if (!identity.IsValid) throw new Phd2IdentityMismatchException(identity);
            // Native set_connected affects ALL selections. Never use it as a
            // generic reconnect for auxiliary mounts, AO or rotators.
            if (identity.Equipment.AuxMount is not null || identity.Equipment.AdaptiveOptics is not null || identity.Equipment.Rotator is not null)
                throw new Phd2Exception("PHD2_OUTPUT_RECONNECT_EXTRA_DEVICES: Native reconnect is restricted to the bound camera and guide mount only.");
            var enabled = await InvokeAsync("get_guide_output_enabled", null, cancellationToken).ConfigureAwait(false);
            if (enabled.ValueKind != System.Text.Json.JsonValueKind.True)
                throw new Phd2Exception("PHD2_GUIDE_OUTPUT_DISABLED: Guide output is disabled or unconfirmed; do not override an operator setting by reconnecting.");

            string? disconnectDiagnostic = null;
            ThrowIfAutomationPaused();
            // Native Selected still permits an active selection exposure loop.
            // Match StopCaptureAndConfirmAsync: only Stopped is idle evidence.
            if (await GetAppStateAsync(cancellationToken).ConfigureAwait(false) != Phd2AppState.Stopped)
                throw new Phd2Exception("PHD2_OUTPUT_RECONNECT_STATE_CHANGED: Capture changed before disconnect; no reconnect was sent.");
            lock (stateGate) { approvedIdentityValidation = null; }
            try { await InvokeAsync("set_connected", new { connected = false }, cancellationToken).ConfigureAwait(false); }
            catch (Phd2RpcException ex) { disconnectDiagnostic = ex.Message; }
            // A returned RPC error may still have released both owners. Only
            // fresh explicit readback can authorize ConnectAll. A timeout or
            // transport loss is never automatically retried.
            var disconnectedResult = await InvokeAsync("get_current_equipment", null, cancellationToken).ConfigureAwait(false);
            // General equipment parsing uses false for unknown connection state.
            // That conservative display default is NOT proof of owner release.
            if (!HasExplicitDisconnectedDevice(disconnectedResult, "camera") ||
                !HasExplicitDisconnectedDevice(disconnectedResult, "mount"))
                throw new Phd2Exception("PHD2_OUTPUT_DISCONNECT_UNCONFIRMED: Both owner releases require explicit disconnected readback; ConnectAll was not sent.");
            var disconnected = new Phd2Equipment(ParseDevice(disconnectedResult, "camera"),
                ParseDevice(disconnectedResult, "mount"), ParseDevice(disconnectedResult, "aux_mount"),
                ParseDevice(disconnectedResult, "AO"), ParseDevice(disconnectedResult, "rotator"));
            UpdateSnapshot(current => current with { Equipment = disconnected });
            if (disconnected.Camera?.Connected != false || disconnected.Mount?.Connected != false ||
                disconnected.Camera.Name != identity.Equipment.Camera?.Name ||
                disconnected.Mount.Name != identity.Equipment.Mount?.Name ||
                disconnected.AuxMount is not null || disconnected.AdaptiveOptics is not null || disconnected.Rotator is not null ||
                await GetProfileAsync(cancellationToken).ConfigureAwait(false) != identity.Profile)
                throw new Phd2Exception("PHD2_OUTPUT_DISCONNECT_UNCONFIRMED: Expected owners were not both released or selection changed; ConnectAll was not sent.");
            ThrowIfAutomationPaused();
            if (await GetAppStateAsync(cancellationToken).ConfigureAwait(false) != Phd2AppState.Stopped)
                throw new Phd2Exception("PHD2_OUTPUT_RECONNECT_STATE_CHANGED: Capture changed while disconnected; ConnectAll was not sent.");
            await InvokeAsync("set_connected", new { connected = true }, cancellationToken).ConfigureAwait(false);
            var restored = await ValidateIdentityAsync(expected, cancellationToken).ConfigureAwait(false);
            if (!restored.IsValid) throw new Phd2IdentityMismatchException(restored);
            if (restored.Profile != identity.Profile || restored.Equipment != identity.Equipment ||
                await GetAppStateAsync(cancellationToken).ConfigureAwait(false) != Phd2AppState.Stopped)
                throw new Phd2Exception("PHD2_OUTPUT_RECONNECT_IDENTITY_CHANGED: Reconnected equipment/state differs; no guiding was started.");
            UpdateSnapshot(current => InvalidateSettle(current with
            {
                ConnectionEpoch = current.ConnectionEpoch + 1,
                GuideOutput = null, LastGuideStep = null, LockPosition = null,
                SelectedStar = null, CalibrationValidation = null,
            }));
            return new(restored.Profile, identity.Equipment, restored.Equipment, Snapshot.ConnectionEpoch,
                ConfirmedStopped: true, CaptureStarted: false, MotionCommandIssued: false, disconnectDiagnostic);
        }
        finally { operationGate.Release(); }
    }

    private static bool HasExplicitDisconnectedDevice(System.Text.Json.JsonElement equipment, string name) =>
        equipment.ValueKind == System.Text.Json.JsonValueKind.Object &&
        equipment.TryGetProperty(name, out var device) &&
        device.ValueKind == System.Text.Json.JsonValueKind.Object &&
        GetOptionalBoolean(device, "connected") == false;
}
