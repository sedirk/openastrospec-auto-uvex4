using UvexAdv.Protocol;

namespace UvexAdv.Core;

public sealed class UvexDeviceController(
    UvexProtocolSession session,
    UvexSafetyOptions options,
    ControlLeaseManager leases) : IDisposable
{
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly SemaphoreSlim motionGate = new(1, 1);
    private int unexpectedTransportRecoveryPending;
    private UvexDeviceStatus status = new()
    {
        PortName = options.PortName,
        Slits = CreateFallbackSlits(options),
    };

    public UvexDeviceStatus Status => status;

    public bool UnexpectedTransportRecoveryPending =>
        Volatile.Read(ref unexpectedTransportRecoveryPending) != 0;

    public event EventHandler<UvexDeviceStatus>? StatusChanged;

    public void RestoreLastKnown(UvexDeviceStatus snapshot)
    {
        if (status.ConnectionState != DeviceConnectionState.Disconnected)
        {
            return;
        }

        SetStatus(snapshot with
        {
            ConnectionState = DeviceConnectionState.Disconnected,
            PortName = options.PortName,
            PositionKnown = false,
            PositionTrust = HasAnyPosition(snapshot) ? UvexPositionTrust.LastKnown : UvexPositionTrust.Unknown,
            SlitIlluminationLedState = UvexOutputState.Unknown,
            SlitIlluminationLedCommandedUtc = null,
            LastError = null,
        });
    }

    public Task ConnectAsync(CancellationToken cancellationToken) =>
        ConnectCoreAsync(isUnexpectedTransportRecovery: false, cancellationToken);

    public async Task<bool> TryRecoverUnexpectedTransportLossAsync(CancellationToken cancellationToken)
    {
        if (!UnexpectedTransportRecoveryPending)
        {
            return false;
        }

        return await ConnectCoreAsync(isUnexpectedTransportRecovery: true, cancellationToken).ConfigureAwait(false);
    }

    public void MarkUnexpectedTransportLoss(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (status.ConnectionState is DeviceConnectionState.Disconnected or DeviceConnectionState.Maintenance)
        {
            return;
        }

        Interlocked.Exchange(ref unexpectedTransportRecoveryPending, 1);
        SetStatus(status with
        {
            ConnectionState = DeviceConnectionState.Faulted,
            PositionKnown = false,
            PositionTrust = HasAnyPosition(status) ? UvexPositionTrust.LastKnown : UvexPositionTrust.Unknown,
            SlitIlluminationLedState = UvexOutputState.Unknown,
            SlitIlluminationLedCommandedUtc = null,
            LastError = $"Unexpected UVEX transport loss on {options.PortName}: {exception.Message}",
        });
    }

    private async Task<bool> ConnectCoreAsync(
        bool isUnexpectedTransportRecovery,
        CancellationToken cancellationToken)
    {
        await connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (isUnexpectedTransportRecovery && !UnexpectedTransportRecoveryPending)
            {
                return false;
            }

            // A user may explicitly request Connect in the short interval
            // after USB/power loss but before the hosted refresh loop observes
            // it.  Convert that stale Ready state into the same recovery path
            // instead of silently treating Connect as a no-op.
            if (!isUnexpectedTransportRecovery &&
                status.ConnectionState == DeviceConnectionState.Ready &&
                !session.IsOpen)
            {
                MarkUnexpectedTransportLoss(
                    new InvalidOperationException($"UVEX transport {options.PortName} is closed."));
            }

            if (!isUnexpectedTransportRecovery)
            {
                Interlocked.Exchange(ref unexpectedTransportRecoveryPending, 0);
            }

            if (status.ConnectionState is not DeviceConnectionState.Disconnected and not DeviceConnectionState.Faulted)
            {
                return false;
            }

            SetStatus(status with
            {
                ConnectionState = DeviceConnectionState.Connecting,
                PositionKnown = false,
                PositionTrust = HasAnyPosition(status) ? UvexPositionTrust.LastKnown : UvexPositionTrust.Unknown,
                SlitIlluminationLedState = UvexOutputState.Unknown,
                SlitIlluminationLedCommandedUtc = null,
                LastError = null,
            });
            try
            {
                // Always close the prior protocol session, even when the
                // SerialPort already reports closed.  A USB/power loss can end
                // the transport without cancelling the old read loop.
                try
                {
                    await session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Opening a fresh, identity-verified COM5 session below is
                    // the recovery authority; stale-session cleanup is best effort.
                }

                await session.OpenAsync(cancellationToken).ConfigureAwait(false);
                SetStatus(status with { ConnectionState = DeviceConnectionState.Initializing });
                await RefreshIdentityAsync(cancellationToken).ConfigureAwait(false);
                await RefreshSlitConfigurationAsync(cancellationToken).ConfigureAwait(false);
                await RefreshPositionsAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref unexpectedTransportRecoveryPending, 0);
                SetStatus(status with { ConnectionState = DeviceConnectionState.Ready, LastError = null });
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    await session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }

                SetStatus(status with
                {
                    ConnectionState = DeviceConnectionState.Faulted,
                    LastError = ex.Message,
                    PositionKnown = false,
                    PositionTrust = HasAnyPosition(status) ? UvexPositionTrust.LastKnown : UvexPositionTrust.Unknown,
                    SlitIlluminationLedState = UvexOutputState.Unknown,
                    SlitIlluminationLedCommandedUtc = null,
                });
                throw;
            }
        }
        finally
        {
            connectionGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref unexpectedTransportRecoveryPending, 0);
            try
            {
                await session.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                SetStatus(status with
                {
                    ConnectionState = DeviceConnectionState.Disconnected,
                    PositionKnown = false,
                    PositionTrust = HasAnyPosition(status) ? UvexPositionTrust.LastKnown : UvexPositionTrust.Unknown,
                    SlitIlluminationLedState = UvexOutputState.Unknown,
                    SlitIlluminationLedCommandedUtc = null,
                });
            }
        }
        finally
        {
            connectionGate.Release();
        }
    }

    public async Task EnterMaintenanceAsync(string leaseToken, CancellationToken cancellationToken)
    {
        leases.Require(leaseToken);
        SetStatus(status with { ConnectionState = DeviceConnectionState.Maintenance, PositionKnown = false, PositionTrust = UvexPositionTrust.LastKnown });
        await session.CloseAsync(cancellationToken).ConfigureAwait(false);
        SetStatus(status with { ConnectionState = DeviceConnectionState.Maintenance, PositionKnown = false, PositionTrust = UvexPositionTrust.LastKnown });
    }

    public async Task ExitMaintenanceAsync(string leaseToken, CancellationToken cancellationToken)
    {
        leases.Require(leaseToken);
        if (status.ConnectionState != DeviceConnectionState.Maintenance)
        {
            return;
        }

        SetStatus(status with { ConnectionState = DeviceConnectionState.Disconnected });
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task HomeGratingAsync(string leaseToken, CancellationToken cancellationToken) =>
        RunMotionAsync(leaseToken, UvexCommands.GratingHome(), UvexCommands.GratingStop(), RefreshPositionsAsync, cancellationToken);

    public async Task MoveGratingRelativeAsync(int deltaSteps, string leaseToken, CancellationToken cancellationToken)
    {
        RequireReadyMotion(leaseToken);
        if (deltaSteps == 0)
        {
            return;
        }

        if (Math.Abs((long)deltaSteps) > options.GratingMaximumSingleMoveSteps)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSteps), "Requested grating move exceeds the configured single-move limit.");
        }

        var current = status.GratingPositionSteps ?? throw new InvalidOperationException("Grating position is unknown.");
        var target = checked(current + deltaSteps);
        if (target < options.GratingMinimumSteps || target > options.GratingMaximumSteps)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSteps), "Requested grating target is outside the configured software travel limits.");
        }

        var command = deltaSteps > 0
            ? UvexCommands.GratingMovePositive(deltaSteps)
            : UvexCommands.GratingMoveNegative(Math.Abs(deltaSteps));
        await RunMotionAsync(leaseToken, command, UvexCommands.GratingStop(), RefreshPositionsAsync, cancellationToken).ConfigureAwait(false);
    }

    public async Task GotoWavelengthAsync(double wavelengthNm, string leaseToken, CancellationToken cancellationToken)
    {
        RequireReadyMotion(leaseToken);
        if (wavelengthNm is < 300 or > 1100)
        {
            throw new ArgumentOutOfRangeException(nameof(wavelengthNm), "Nominal wavelength must be between 300 and 1100 nm.");
        }

        var angstrom = checked((int)Math.Round(wavelengthNm * 10, MidpointRounding.AwayFromZero));
        await RunMotionAsync(leaseToken, UvexCommands.GratingGotoWavelengthAngstrom(angstrom), UvexCommands.GratingStop(), RefreshPositionsAsync, cancellationToken).ConfigureAwait(false);
    }

    public Task HomeFocusAsync(string leaseToken, CancellationToken cancellationToken) =>
        RunMotionAsync(leaseToken, UvexCommands.FocusHome(), UvexCommands.FocusStop(), RefreshPositionsAsync, cancellationToken);

    public async Task MoveFocusRelativeAsync(int deltaSteps, string leaseToken, CancellationToken cancellationToken)
    {
        RequireReadyMotion(leaseToken);
        if (deltaSteps == 0)
        {
            return;
        }

        if (Math.Abs((long)deltaSteps) > options.FocusMaximumSingleMoveSteps)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSteps), "Requested focus move exceeds the configured single-move limit.");
        }

        var current = status.FocusPositionSteps ?? throw new InvalidOperationException("Focus position is unknown.");
        var target = checked(current + deltaSteps);
        if (target < options.FocusMinimumSteps || target > options.FocusMaximumSteps)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSteps), "Requested focus target is outside the configured software travel limits.");
        }

        // The production firmware reports an increasing FPOS for FGIN and a
        // decreasing FPOS for FGOU. Keep the public delta contract aligned with
        // the reported position rather than the optical "in/out" labels.
        var command = deltaSteps > 0
            ? UvexCommands.FocusIn(deltaSteps)
            : UvexCommands.FocusOut(Math.Abs(deltaSteps));
        await RunMotionAsync(
            leaseToken,
            command,
            UvexCommands.FocusStop(),
            RefreshPositionsAsync,
            cancellationToken,
            expectedFocusPositionSteps: target).ConfigureAwait(false);
    }

    public async Task SelectSlitAsync(int position, string leaseToken, CancellationToken cancellationToken)
    {
        await SelectSlitAsync(
            position,
            options.UseSlitPhotodiode,
            leaseToken,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SelectSlitAsync(
        int position,
        bool usePhotodiode,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        RequireReadyMotion(leaseToken);
        if (position < 1 || position > options.SlitPositions)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        await RunMotionAsync(
            leaseToken,
            UvexCommands.SlitMove(position, usePhotodiode),
            UvexCommands.SlitStop(),
            RefreshPositionsAsync,
            cancellationToken,
            expectedSlitPosition: position).ConfigureAwait(false);
    }

    public async Task CalibrateSlitPositionAsync(int position, string leaseToken, CancellationToken cancellationToken)
    {
        RequireReady(leaseToken);
        if (position < 1 || position > options.SlitPositions)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        await session.SendAsync(UvexCommands.SlitCalibratePosition(position), cancellationToken).ConfigureAwait(false);
        await RefreshPositionsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AutoCalibrateSlitPhotodiodeAsync(string leaseToken, CancellationToken cancellationToken)
    {
        RequireReady(leaseToken);
        if (!status.Capabilities.HasFlag(UvexCapabilities.SlitPhotodiode))
        {
            throw new NotSupportedException("This UVEX does not report the slit photodiode option.");
        }

        await session.SendAsync(UvexCommands.SlitAutoCalibratePhotodiode(), cancellationToken).ConfigureAwait(false);
        await RefreshSlitDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetSlitOffsetAsync(int position, int offsetSteps, string leaseToken, CancellationToken cancellationToken)
    {
        RequireReady(leaseToken);
        if (position < 1 || position > options.SlitPositions)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (Math.Abs((long)offsetSteps) > options.SlitOffsetMaximumAbsoluteSteps)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offsetSteps),
                $"Slit offset exceeds the configured +/-{options.SlitOffsetMaximumAbsoluteSteps} step limit.");
        }

        await session.SendAsync(UvexCommands.SlitSetOffset(position, offsetSteps), cancellationToken).ConfigureAwait(false);
        await RefreshSlitConfigurationAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetSlitIlluminationAsync(bool enabled, string leaseToken, CancellationToken cancellationToken)
    {
        RequireReady(leaseToken);
        if (!status.Capabilities.HasFlag(UvexCapabilities.SlitPhotodiode))
        {
            throw new NotSupportedException(
                "This UVEX does not report the slit-wheel LED/photodiode option (IST0 bit 5).");
        }

        var command = enabled
            ? UvexCommands.SlitIlluminationOn()
            : UvexCommands.SlitIlluminationOff();
        await session.SendAsync(command, cancellationToken).ConfigureAwait(false);
        SetStatus(status with
        {
            SlitIlluminationLedState = enabled ? UvexOutputState.On : UvexOutputState.Off,
            SlitIlluminationLedCommandedUtc = DateTimeOffset.UtcNow,
        });
        await RefreshSlitDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetCalibrationRelayAsync(int relay, bool enabled, string leaseToken, CancellationToken cancellationToken)
    {
        leases.Require(leaseToken);
        if (status.ConnectionState != DeviceConnectionState.Ready)
        {
            throw new InvalidOperationException("UVEX is not ready.");
        }

        if (!status.Capabilities.HasFlag(UvexCapabilities.Calibration))
        {
            throw new NotSupportedException("This UVEX configuration does not report a Calibrex/calibration module.");
        }

        if (relay is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(relay));
        }

        await session.SendAsync(UvexCommands.CalibrationRelay(relay, enabled), cancellationToken).ConfigureAwait(false);
    }

    public async Task EmergencyStopAsync(CancellationToken cancellationToken)
    {
        await session.SendAsync(UvexCommands.GratingStop(), cancellationToken).ConfigureAwait(false);
        await session.SendAsync(UvexCommands.FocusStop(), cancellationToken).ConfigureAwait(false);
        await session.SendAsync(UvexCommands.SlitStop(), cancellationToken).ConfigureAwait(false);
        if (status.Capabilities.HasFlag(UvexCapabilities.SlitPhotodiode))
        {
            await session.SendAsync(UvexCommands.SlitIlluminationOff(), cancellationToken).ConfigureAwait(false);
        }

        SetStatus(status with
        {
            ConnectionState = DeviceConnectionState.Ready,
            SlitIlluminationLedState = status.Capabilities.HasFlag(UvexCapabilities.SlitPhotodiode)
                ? UvexOutputState.Off
                : UvexOutputState.Unknown,
            SlitIlluminationLedCommandedUtc = status.Capabilities.HasFlag(UvexCapabilities.SlitPhotodiode)
                ? DateTimeOffset.UtcNow
                : null,
            LastError = "Emergency stop requested; the slit positioning LED was switched off when supported.",
        });
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        EnsureTransportOpenOrMarkLost();
        await RefreshPositionsAsync(cancellationToken).ConfigureAwait(false);
        await RefreshSlitDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        var temperature = await session.SendAsync(UvexCommands.Temperature(), cancellationToken).ConfigureAwait(false);
        if (temperature?.TryGetDouble(0, out var value) == true)
        {
            SetStatus(status with { TemperatureC = value });
        }
    }

    public void Dispose()
    {
        connectionGate.Dispose();
        motionGate.Dispose();
    }

    private async Task RefreshIdentityAsync(CancellationToken cancellationToken)
    {
        var ping = await session.SendAsync(UvexCommands.Ping(), cancellationToken).ConfigureAwait(false);
        if (ping is null)
        {
            throw new InvalidOperationException("UVEX did not acknowledge ISLV.");
        }

        var firmware = await session.SendAsync(UvexCommands.FirmwareVersion(), cancellationToken).ConfigureAwait(false);
        var description = await session.SendAsync(UvexCommands.Description(), cancellationToken).ConfigureAwait(false);
        var configuration = await session.SendAsync(UvexCommands.Configuration(), cancellationToken).ConfigureAwait(false);
        var capabilities = UvexCapabilities.None;
        if (configuration?.TryGetInt32(0, out var bitMask) == true)
        {
            capabilities = (UvexCapabilities)bitMask;
        }

        SetStatus(status with
        {
            FirmwareVersion = firmware?.Arguments.FirstOrDefault(),
            Description = description is null ? null : string.Join(' ', description.Arguments),
            Capabilities = capabilities,
        });
    }

    private async Task RefreshPositionsAsync(CancellationToken cancellationToken)
    {
        var grating = await session.SendAsync(UvexCommands.GratingPosition(), cancellationToken).ConfigureAwait(false);
        var focus = await session.SendAsync(UvexCommands.FocusPosition(), cancellationToken).ConfigureAwait(false);
        var slit = await session.SendAsync(UvexCommands.SlitPosition(), cancellationToken).ConfigureAwait(false);
        var slitMotor = await TryOptionalQueryAsync(UvexCommands.SlitMotorPosition(), cancellationToken).ConfigureAwait(false);

        var gratingSteps = GetInt32(grating, 0);
        var central = GetDouble(grating, 1);
        var minimum = GetDouble(grating, 2);
        var maximum = GetDouble(grating, 3);
        var focusSteps = GetInt32(focus, 0);
        var slitPosition = GetInt32(slit, 0);
        var slitMotorSteps = GetInt32(slitMotor, 0);
        var allKnown = gratingSteps.HasValue && focusSteps.HasValue && slitPosition.HasValue;

        SetStatus(status with
        {
            GratingPositionSteps = gratingSteps,
            CentralWavelengthAngstrom = central,
            MinimumWavelengthAngstrom = minimum,
            MaximumWavelengthAngstrom = maximum,
            FocusPositionSteps = focusSteps,
            SlitPosition = slitPosition,
            SlitMotorPositionSteps = slitMotorSteps,
            PositionKnown = allKnown,
            PositionTrust = allKnown ? UvexPositionTrust.Live : UvexPositionTrust.Unknown,
            PositionMeasuredUtc = allKnown ? DateTimeOffset.UtcNow : status.PositionMeasuredUtc,
        });
    }

    private async Task RefreshSlitConfigurationAsync(CancellationToken cancellationToken)
    {
        var maximum = await TryOptionalQueryAsync(UvexCommands.SlitMaximum(), cancellationToken).ConfigureAwait(false);
        var names = await TryOptionalQueryAsync(UvexCommands.SlitNames(), cancellationToken).ConfigureAwait(false);
        var count = Math.Clamp(GetInt32(maximum, 0) ?? options.SlitPositions, 1, 10);
        var slits = new List<UvexSlitDefinition>(count);
        for (var position = 1; position <= count; position++)
        {
            var offset = await TryOptionalQueryAsync(UvexCommands.SlitOffset(position), cancellationToken).ConfigureAwait(false);
            var configuredName = position <= options.SlitNames.Length ? options.SlitNames[position - 1] : $"Slit {position}";
            var deviceName = names is not null && names.Arguments.Count >= position ? names.Arguments[position - 1] : null;
            var offsetIndex = offset?.Arguments.Count > 1 ? 1 : 0;
            slits.Add(new UvexSlitDefinition(
                position,
                string.IsNullOrWhiteSpace(deviceName) ? configuredName : deviceName,
                GetInt32(offset, offsetIndex)));
        }

        SetStatus(status with { Slits = slits });
        await RefreshSlitDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshSlitDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (!status.Capabilities.HasFlag(UvexCapabilities.SlitPhotodiode))
        {
            return;
        }

        var value = await TryOptionalQueryAsync(UvexCommands.SlitPhotodiodeValue(), cancellationToken).ConfigureAwait(false);
        var threshold = await TryOptionalQueryAsync(UvexCommands.SlitPhotodiodeThreshold(), cancellationToken).ConfigureAwait(false);
        var enabled = await TryOptionalQueryAsync(UvexCommands.SlitPhotodiodeEnabled(), cancellationToken).ConfigureAwait(false);
        SetStatus(status with
        {
            SlitPhotodiodeValue = GetInt32(value, 0),
            SlitPhotodiodeThreshold = GetInt32(threshold, 0),
            SlitPhotodiodeEnabled = GetInt32(enabled, 0) is { } active ? active == 1 : null,
        });
    }

    private async Task RunMotionAsync(
        string leaseToken,
        UvexCommand motion,
        UvexCommand stop,
        Func<CancellationToken, Task> refresh,
        CancellationToken cancellationToken,
        int? expectedFocusPositionSteps = null,
        int? expectedSlitPosition = null)
    {
        RequireReadyMotion(leaseToken);
        await motionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SetStatus(status with { ConnectionState = DeviceConnectionState.Busy, LastError = null });
        try
        {
            await SendMotionAndWaitUntilIdleAsync(leaseToken, motion, cancellationToken).ConfigureAwait(false);
            if (options.SerialPostMotionSettleDelay > TimeSpan.Zero)
            {
                await Task.Delay(options.SerialPostMotionSettleDelay, cancellationToken).ConfigureAwait(false);
            }

            await refresh(cancellationToken).ConfigureAwait(false);
            if (expectedFocusPositionSteps.HasValue && status.FocusPositionSteps != expectedFocusPositionSteps)
            {
                throw new InvalidOperationException(
                    $"Focus readback {status.FocusPositionSteps?.ToString() ?? "unknown"} does not match requested position {expectedFocusPositionSteps.Value}.");
            }

            if (expectedSlitPosition.HasValue && status.SlitPosition != expectedSlitPosition)
            {
                throw new InvalidOperationException(
                    $"Slit readback {status.SlitPosition?.ToString() ?? "unknown"} does not match requested position {expectedSlitPosition.Value}.");
            }

            SetStatus(status with { ConnectionState = DeviceConnectionState.Ready });
        }
        catch
        {
            try
            {
                await session.SendAsync(stop, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }

            SetStatus(status with { ConnectionState = DeviceConnectionState.Faulted, PositionKnown = false, LastError = "Motion failed; stop was requested and position must be revalidated." });
            throw;
        }
        finally
        {
            motionGate.Release();
        }
    }

    private async Task SendMotionAndWaitUntilIdleAsync(
        string leaseToken,
        UvexCommand motion,
        CancellationToken cancellationToken)
    {
        var motionWire = motion.ToWireString();
        var sawCommandEcho = 0;
        var sawBusy = 0;
        var completion = new TaskCompletionSource<UvexFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

        void HandleUnsolicitedFrame(object? sender, UvexFrame frame)
        {
            if (string.Equals(frame.Raw, motionWire, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Exchange(ref sawCommandEcho, 1);
                return;
            }

            if (Volatile.Read(ref sawCommandEcho) == 0)
            {
                // Ignore stale frames that were already buffered before this
                // command's controller echo established the motion epoch.
                return;
            }

            if (frame.Code == "IBSY" && frame.TryGetInt32(0, out var available))
            {
                if (available == 0)
                {
                    Interlocked.Exchange(ref sawBusy, 1);
                }
                else if (available == 1 && Volatile.Read(ref sawBusy) == 1)
                {
                    completion.TrySetResult(frame);
                }

                return;
            }

            if (frame.Code is "IERR" or "IALE")
            {
                completion.TrySetException(new UvexProtocolException(
                    frame.Code,
                    $"UVEX returned {frame.Code} during {motion.Code}: {string.Join(';', frame.Arguments)}"));
            }
        }

        session.UnsolicitedFrame += HandleUnsolicitedFrame;
        using var timeout = new CancellationTokenSource(options.MotionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // Motion commands echo themselves, emit IBSY;0 when accepted, and
            // later emit IBSY;1 when the controller is available again. Real
            // firmware does not service an IBSY query while this slit motion
            // is active, so consume the unsolicited transition instead.
            // Subscribe before sending so a short motion cannot win the race.
            await session.SendAsync(motion, cancellationToken).ConfigureAwait(false);

            while (!completion.Task.IsCompleted)
            {
                leases.Require(leaseToken);
                var leaseCheckDelay = Task.Delay(TimeSpan.FromMilliseconds(250), linked.Token);
                var finished = await Task.WhenAny(completion.Task, leaseCheckDelay).ConfigureAwait(false);
                if (ReferenceEquals(finished, completion.Task))
                {
                    break;
                }

                await leaseCheckDelay.ConfigureAwait(false);
            }

            leases.Require(leaseToken);
            await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"UVEX motion {motion.Code} did not report the IBSY;0 -> IBSY;1 completion transition within {options.MotionTimeout}.");
        }
        finally
        {
            session.UnsolicitedFrame -= HandleUnsolicitedFrame;
        }
    }

    private void RequireReadyMotion(string leaseToken)
    {
        RequireReady(leaseToken);
        options.ValidateForMotion();
        if (!status.PositionKnown || status.PositionTrust != UvexPositionTrust.Live)
        {
            throw new InvalidOperationException("UVEX is not ready or its position is unknown.");
        }
    }

    private void RequireReady(string leaseToken)
    {
        leases.Require(leaseToken);
        if (status.ConnectionState != DeviceConnectionState.Ready)
        {
            throw new InvalidOperationException("UVEX is not ready.");
        }

        EnsureTransportOpenOrMarkLost();
    }

    private void EnsureTransportOpenOrMarkLost()
    {
        if (session.IsOpen)
        {
            return;
        }

        var exception = new InvalidOperationException($"UVEX transport {options.PortName} is closed.");
        MarkUnexpectedTransportLoss(exception);
        throw exception;
    }

    private void SetStatus(UvexDeviceStatus next)
    {
        status = next with { TimestampUtc = DateTimeOffset.UtcNow };
        StatusChanged?.Invoke(this, status);
    }

    private static IReadOnlyList<UvexSlitDefinition> CreateFallbackSlits(UvexSafetyOptions safety) =>
        Enumerable.Range(1, Math.Clamp(safety.SlitPositions, 1, 10))
            .Select(position => new UvexSlitDefinition(
                position,
                position <= safety.SlitNames.Length ? safety.SlitNames[position - 1] : $"Slit {position}",
                null))
            .ToArray();

    private static bool HasAnyPosition(UvexDeviceStatus value) =>
        value.GratingPositionSteps.HasValue || value.FocusPositionSteps.HasValue || value.SlitPosition.HasValue;

    private static int? GetInt32(UvexFrame? frame, int index) =>
        frame?.TryGetInt32(index, out var value) == true ? value : null;

    private static double? GetDouble(UvexFrame? frame, int index) =>
        frame?.TryGetDouble(index, out var value) == true ? value : null;

    private async Task<UvexFrame?> TryOptionalQueryAsync(UvexCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return await session.SendAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or UvexProtocolException)
        {
            return null;
        }
    }
}
