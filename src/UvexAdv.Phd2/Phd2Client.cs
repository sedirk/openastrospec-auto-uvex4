using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UvexAdv.Phd2;

public sealed class Phd2Client : IPhd2Client
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Phd2ClientOptions options;
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pendingRequests = new();
    private readonly object transportGate = new();
    private readonly object stateGate = new();
    private readonly object eventWaiterGate = new();
    private readonly Dictionary<Guid, EventWaiter> eventWaiters = [];

    private TcpClient? tcpClient;
    private StreamReader? reader;
    private StreamWriter? writer;
    private CancellationTokenSource? connectionCancellation;
    private Task? readerTask;
    private Phd2StateSnapshot snapshot = Phd2StateSnapshot.Disconnected;
    private Phd2IdentityValidation? approvedIdentityValidation;
    private long nextRequestId;
    private long nextEventSequence;
    private long nextSettleOperationId;
    private bool disposed;

    public Phd2Client(Phd2ClientOptions? options = null)
    {
        this.options = options ?? new Phd2ClientOptions();
        this.options.Validate();
    }

    public event EventHandler<Phd2EventMessage>? EventReceived;

    public event EventHandler<Phd2StateSnapshot>? SnapshotChanged;

    public bool IsConnected => Snapshot.IsConnected;

    public bool IsAutomationPaused => Snapshot.AutomationPaused;

    public Phd2StateSnapshot Snapshot
    {
        get
        {
            lock (stateGate)
            {
                return snapshot;
            }
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(options.Host, options.Port, cancellationToken).ConfigureAwait(false);
                var stream = client.GetStream();
                var localReader = new StreamReader(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);
                var localWriter = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4096,
                    leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\r\n",
                };
                var lifetime = new CancellationTokenSource();
                var startReader = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var localReaderTask = ReadLoopAsync(localReader, startReader.Task, lifetime.Token);

                lock (transportGate)
                {
                    tcpClient = client;
                    reader = localReader;
                    writer = localWriter;
                    connectionCancellation = lifetime;
                    readerTask = localReaderTask;
                }

                UpdateSnapshot(current => current with
                {
                    IsConnected = true,
                    // A business-level pause is owned by the automation
                    // coordinator, not by one TCP connection. Reconnecting
                    // must not silently re-enable mutations.
                    AutomationPaused = current.AutomationPaused,
                    Phd2Paused = false,
                    AppState = Phd2AppState.Unknown,
                    CalibrationValidation = null,
                    LastSettle = null,
                    SettleProgress = null,
                    ConnectionEpoch = checked(current.ConnectionEpoch + 1),
                    GuideEpoch = checked(current.GuideEpoch + 1),
                    PendingSettleOperationId = null,
                    PendingSettleConnectionEpoch = null,
                    PendingSettleGuideEpoch = null,
                    PendingSettleArmedAfterSequence = null,
                    PendingSettleBeginSequence = null,
                    PendingSettleCommandAccepted = false,
                    PendingTakeoverLoopStopAllowed = false,
                    PendingLateLoopFrameAllowed = current.AppState == Phd2AppState.Looping,
                    PendingForceRecalibration = false,
                    PendingCalibrationStartSequence = null,
                    PendingCalibrationTerminalSequence = null,
                    LastSettleOperationId = null,
                    LastSettleCommandAccepted = false,
                    LastSettleConnectionEpoch = null,
                    LastSettleGuideEpoch = null,
                    LastProtocolError = null,
                });
                lock (stateGate)
                {
                    approvedIdentityValidation = null;
                }
                startReader.TrySetResult(true);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not Phd2Exception)
        {
            throw new Phd2DisconnectedException(
                $"Could not connect to the PHD2 event server at {options.Host}:{options.Port}.",
                ex);
        }
        finally
        {
            connectionGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        Task? localReaderTask;
        CancellationTokenSource? localCancellation;
        TcpClient? localClient;
        StreamReader? localReader;
        StreamWriter? localWriter;

        await connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (transportGate)
            {
                localReaderTask = readerTask;
                localCancellation = connectionCancellation;
                localClient = tcpClient;
                localReader = reader;
                localWriter = writer;

                readerTask = null;
                connectionCancellation = null;
                tcpClient = null;
                reader = null;
                writer = null;
            }

            localCancellation?.Cancel();
            localClient?.Dispose();
            localReader?.Dispose();
            localWriter?.Dispose();
            CompleteOutstanding(new Phd2DisconnectedException("The PHD2 event-server connection was closed."));
            MarkDisconnected();
        }
        finally
        {
            connectionGate.Release();
        }

        if (localReaderTask is not null)
        {
            try
            {
                await localReaderTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (localCancellation?.IsCancellationRequested == true)
            {
            }
            catch (Phd2DisconnectedException)
            {
            }
        }

        localCancellation?.Dispose();
    }

    public void PauseAutomation()
    {
        ThrowIfDisposed();
        UpdateSnapshot(current => InvalidateSettle(current with { AutomationPaused = true }));
    }

    public void ResumeAutomation()
    {
        ThrowIfDisposed();
        UpdateSnapshot(current => InvalidateSettle(current with { AutomationPaused = false }));
    }

    public async Task<Phd2Profile> GetProfileAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync("get_profile", parameters: null, cancellationToken).ConfigureAwait(false);
        var profile = new Phd2Profile(
            GetRequiredInt32(result, "id"),
            GetRequiredString(result, "name"));
        UpdateSnapshot(current => current with
        {
            Profile = profile,
            CalibrationValidation = current.CalibrationValidation?.Profile == profile
                ? current.CalibrationValidation
                : null,
        });
        lock (stateGate)
        {
            if (approvedIdentityValidation?.Profile != profile)
            {
                approvedIdentityValidation = null;
            }
        }
        return profile;
    }

    public async Task<Phd2Equipment> GetCurrentEquipmentAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync("get_current_equipment", parameters: null, cancellationToken).ConfigureAwait(false);
        var equipment = new Phd2Equipment(
            ParseDevice(result, "camera"),
            ParseDevice(result, "mount"),
            ParseDevice(result, "aux_mount"),
            ParseDevice(result, "AO"),
            ParseDevice(result, "rotator"));
        UpdateSnapshot(current => current with { Equipment = equipment });
        lock (stateGate)
        {
            if (approvedIdentityValidation?.Equipment != equipment)
            {
                approvedIdentityValidation = null;
            }
        }
        return equipment;
    }

    public async Task<Phd2IdentityValidation> ValidateIdentityAsync(
        Phd2IdentityRequirement requirement,
        CancellationToken cancellationToken)
    {
        ValidateIdentityRequirement(requirement);
        var profileTask = GetProfileAsync(cancellationToken);
        var equipmentTask = GetCurrentEquipmentAsync(cancellationToken);
        await Task.WhenAll(profileTask, equipmentTask).ConfigureAwait(false);

        var profile = await profileTask.ConfigureAwait(false);
        var equipment = await equipmentTask.ConfigureAwait(false);
        var failures = new List<string>();
        var indeterminate = new List<string>();

        if (profile.Id != requirement.ProfileId)
        {
            failures.Add($"profile id is {profile.Id}, expected {requirement.ProfileId}");
        }

        if (!string.Equals(profile.Name, requirement.ProfileName, StringComparison.Ordinal))
        {
            failures.Add($"profile name is '{profile.Name}', expected '{requirement.ProfileName}'");
        }

        ValidateDevice("camera", equipment.Camera, requirement.CameraName, requirement.RequireConnected, failures);
        ValidateDevice("mount", equipment.Mount, requirement.MountName, requirement.RequireConnected, failures);
        if (!string.IsNullOrWhiteSpace(requirement.StableCameraId))
        {
            if (string.IsNullOrWhiteSpace(equipment.Camera?.StableId))
            {
                indeterminate.Add(
                    $"PHD2 get_current_equipment does not expose the stable camera id '{requirement.StableCameraId}'");
            }
            else if (!string.Equals(equipment.Camera.StableId, requirement.StableCameraId, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    $"camera stable id is '{equipment.Camera.StableId}', expected '{requirement.StableCameraId}'");
            }
        }

        var validation = new Phd2IdentityValidation(profile, equipment, failures, indeterminate);
        lock (stateGate)
        {
            approvedIdentityValidation = validation.IsValid ? validation : null;
        }

        return validation;
    }

    public async Task EnsureIdentityAsync(
        Phd2IdentityRequirement requirement,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateIdentityAsync(requirement, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new Phd2IdentityMismatchException(validation);
        }
    }

    public async Task<Phd2CalibrationData> GetCalibrationDataAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(
                "get_calibration_data",
                new[] { "Mount" },
                cancellationToken)
            .ConfigureAwait(false);
        var calibrated = GetRequiredBoolean(result, "calibrated");
        return new Phd2CalibrationData(
            calibrated,
            GetOptionalDouble(result, "xAngle"),
            GetOptionalDouble(result, "xRate"),
            GetOptionalString(result, "xParity"),
            GetOptionalDouble(result, "yAngle"),
            GetOptionalDouble(result, "yRate"),
            GetOptionalString(result, "yParity"),
            GetOptionalDouble(result, "declination"));
    }

    public async Task<Phd2CalibrationValidation> ValidateCalibrationAsync(
        Phd2CalibrationRequirement requirement,
        CancellationToken cancellationToken)
    {
        ValidateCalibrationRequirement(requirement);

        var profileBefore = await GetProfileAsync(cancellationToken).ConfigureAwait(false);
        var calibration = await GetCalibrationDataAsync(cancellationToken).ConfigureAwait(false);
        var profileAfter = await GetProfileAsync(cancellationToken).ConfigureAwait(false);
        var evaluatedUtc = DateTimeOffset.UtcNow;
        var failures = new List<string>();
        var indeterminate = new List<string>();

        if (profileBefore != profileAfter)
        {
            failures.Add(
                $"PHD2 profile changed during calibration validation from " +
                $"{profileBefore.Id}/'{profileBefore.Name}' to {profileAfter.Id}/'{profileAfter.Name}'");
        }

        if (profileAfter.Id != requirement.ProfileId ||
            !string.Equals(profileAfter.Name, requirement.ProfileName, StringComparison.Ordinal))
        {
            failures.Add(
                $"calibration belongs to current profile {profileAfter.Id}/'{profileAfter.Name}', expected " +
                $"{requirement.ProfileId}/'{requirement.ProfileName}'");
        }

        TimeSpan? age = null;
        if (requirement.CalibrationTimestampUtc.HasValue)
        {
            age = evaluatedUtc - requirement.CalibrationTimestampUtc.Value;
            if (age < TimeSpan.Zero)
            {
                failures.Add("calibration timestamp is in the future");
            }
            else if (age > requirement.MaximumAge)
            {
                failures.Add($"calibration age {age.Value} exceeds the limit {requirement.MaximumAge}");
            }
        }
        else if (requirement.RequireKnownAge)
        {
            indeterminate.Add(
                "PHD2 get_calibration_data does not expose calibration age; a trusted timestamp was not supplied");
        }

        double? orthogonalityError = null;
        if (!calibration.Calibrated)
        {
            failures.Add("mount calibration is absent");
        }
        else
        {
            ValidateAxisRate(
                "RA",
                calibration.RaRatePixelsPerSecond,
                requirement.MinimumAxisRatePixelsPerSecond,
                requirement.MaximumAxisRatePixelsPerSecond,
                failures);
            ValidateAxisRate(
                "Dec",
                calibration.DecRatePixelsPerSecond,
                requirement.MinimumAxisRatePixelsPerSecond,
                requirement.MaximumAxisRatePixelsPerSecond,
                failures);

            if (!IsFinite(calibration.RaAngleDegrees) || !IsFinite(calibration.DecAngleDegrees))
            {
                failures.Add("calibration RA/Dec angles are missing or non-finite");
            }
            else
            {
                orthogonalityError = CalculateOrthogonalityErrorDegrees(
                    calibration.RaAngleDegrees!.Value,
                    calibration.DecAngleDegrees!.Value);
                if (orthogonalityError > requirement.MaximumOrthogonalityErrorDegrees)
                {
                    failures.Add(
                        $"calibration orthogonality error is {orthogonalityError.Value:F1}°, " +
                        $"above the limit {requirement.MaximumOrthogonalityErrorDegrees:F1}°");
                }
            }
        }

        var validation = new Phd2CalibrationValidation(
            profileAfter,
            calibration,
            evaluatedUtc,
            age,
            orthogonalityError,
            failures,
            indeterminate);
        UpdateSnapshot(current => current with { CalibrationValidation = validation });
        return validation;
    }

    public async Task EnsureCalibrationSaneAsync(
        Phd2CalibrationRequirement requirement,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateCalibrationAsync(requirement, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new Phd2CalibrationRejectedException(
                $"PHD2 calibration sanity gate returned {validation.Status}: " +
                string.Join("; ", validation.Failures.Concat(validation.IndeterminateReasons)),
                validation);
        }
    }

    public async Task<Phd2SingleFrameResult> CaptureFullFrameAsync(
        Phd2SingleFrameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSingleFrameRequest(request);

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAutomationPaused();
            var destinationPath = Path.GetFullPath(request.DestinationPath);
            return await CaptureUsingLoopSaveAsync(
                    request with { DestinationPath = destinationPath },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await BestEffortStopCaptureAsync().ConfigureAwait(false);
            throw;
        }
        catch (Phd2CommandTimeoutException)
        {
            await BestEffortStopCaptureAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<Phd2SingleFrameResult> CaptureSingleFrameWithParametersAsync(
        Phd2SingleFrameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSingleFrameRequest(request);

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAutomationPaused();
            var destinationPath = Path.GetFullPath(request.DestinationPath);
            if (File.Exists(destinationPath))
            {
                throw new Phd2CaptureException(
                    $"PHD2 native single-frame destination already exists: '{destinationPath}'.");
            }

            var appState = await GetAppStateAsync(cancellationToken).ConfigureAwait(false);
            if (appState is not Phd2AppState.Stopped and not Phd2AppState.Selected)
            {
                throw new Phd2CaptureException(
                    $"PHD2 native single-frame acquisition requires an idle Stopped or Selected state; current state is {appState}. " +
                    "The existing capture, calibration, or guiding session was left untouched.");
            }

            var baseline = Snapshot;
            using var completedWaiter = RegisterEventWaiter(message =>
                message.Name == "SingleFrameComplete" &&
                message.Sequence > baseline.EventSequence);

            try
            {
                await InvokeAsync(
                        "capture_single_frame",
                        new
                        {
                            exposure = request.ExposureMs,
                            binning = request.Binning,
                            gain = request.GainPercent,
                            path = destinationPath,
                            save = true,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Phd2RpcException ex) when (ex.Code == -32601)
            {
                throw new Phd2CaptureException(
                    "This PHD2 build does not expose capture_single_frame; exact per-frame gain/binning cannot be applied and no profile-gain fallback was attempted.");
            }

            var completed = await WaitForEventAsync(
                    completedWaiter,
                    "native single-frame capture",
                    ExposureBoundLoopingFrameTimeout(request.ExposureMs),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!GetRequiredBoolean(completed.Payload, "Success"))
            {
                var error = GetOptionalString(completed.Payload, "Error") ?? "PHD2 reported an unspecified capture failure.";
                throw new Phd2CaptureException($"PHD2 native single-frame capture failed: {error}");
            }

            var reportedPath = GetRequiredString(completed.Payload, "Path");
            if (!Path.GetFullPath(reportedPath).Equals(destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new Phd2CaptureException(
                    $"PHD2 native single-frame event reported '{reportedPath}', expected '{destinationPath}'.");
            }

            await WaitForFileReadyAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            var stateAfter = await GetAppStateAsync(cancellationToken).ConfigureAwait(false);
            if (stateAfter is not Phd2AppState.Stopped and not Phd2AppState.Selected ||
                !Snapshot.IsConnected || Snapshot.ConnectionEpoch != baseline.ConnectionEpoch)
            {
                throw new Phd2CaptureException(
                    $"PHD2 did not remain in the original idle connection epoch after native single-frame capture (state {stateAfter}).");
            }

            var result = new Phd2SingleFrameResult(
                destinationPath,
                UsedLoopSaveFallback: false,
                RequestedParametersApplied: true,
                completed.ReceivedUtc,
                VerifiedExposureMilliseconds: request.ExposureMs,
                AutomaticRetryAllowed: false);
            UpdateSnapshot(current => current with { LastSingleFrame = result });
            return result;
        }
        catch (OperationCanceledException)
        {
            await BestEffortStopCaptureAsync().ConfigureAwait(false);
            throw;
        }
        catch (Phd2CommandTimeoutException)
        {
            await BestEffortStopCaptureAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<Phd2SingleFrameResult> SaveNextLoopingFrameAsync(
        Phd2SingleFrameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSingleFrameRequest(request);

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAutomationPaused();
            var destinationPath = Path.GetFullPath(request.DestinationPath);
            var appState = await GetAppStateAsync(cancellationToken).ConfigureAwait(false);
            if (appState != Phd2AppState.Looping)
            {
                throw new Phd2CaptureException(
                    $"PHD2 continuous full-frame save requires an existing Looping state; current state is {appState}. " +
                    "No exposure, loop, stop, or save command was sent.");
            }

            var exposureReadback = await InvokeAsync(
                    "get_exposure",
                    parameters: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (exposureReadback.ValueKind != JsonValueKind.Number ||
                !exposureReadback.TryGetInt32(out var verifiedExposureMilliseconds) ||
                verifiedExposureMilliseconds != request.ExposureMs)
            {
                throw new Phd2CaptureException(
                    $"PHD2 exposure readback did not exactly match the commissioned {request.ExposureMs}ms " +
                    "continuous-loop exposure. The running loop was left untouched.");
            }

            var baseline = Snapshot;
            using var frameWaiter = RegisterEventWaiter(message =>
                message.Name == "LoopingExposures" &&
                message.Sequence > baseline.EventSequence);
            var frameTimeout = ExposureBoundLoopingFrameTimeout(request.ExposureMs);
            var frameEvent = await WaitForEventAsync(
                    frameWaiter,
                    "fresh continuous-loop frame",
                    frameTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            var beforeSave = Snapshot;
            if (!beforeSave.IsConnected || beforeSave.AutomationPaused ||
                beforeSave.ConnectionEpoch != baseline.ConnectionEpoch ||
                beforeSave.AppState != Phd2AppState.Looping ||
                beforeSave.EventSequence < frameEvent.Sequence)
            {
                throw new Phd2CaptureException(
                    "PHD2 did not remain in the same connected Looping epoch before save_image. " +
                    "The caller must perform checked stop cleanup; no save was attempted.");
            }

            var saveResult = await InvokeAsync("save_image", parameters: null, cancellationToken)
                .ConfigureAwait(false);
            var sourcePath = GetRequiredString(saveResult, "filename");
            await CopySavedImageToImmutableEvidenceAsync(
                    sourcePath,
                    destinationPath,
                    cancellationToken)
                .ConfigureAwait(false);

            var afterSave = Snapshot;
            if (!afterSave.IsConnected || afterSave.AutomationPaused ||
                afterSave.ConnectionEpoch != baseline.ConnectionEpoch ||
                afterSave.AppState != Phd2AppState.Looping)
            {
                throw new Phd2CaptureException(
                    "PHD2 continuous loop changed while copying immutable evidence. " +
                    "The saved frame must not authorize a subsequent action.");
            }

            var result = new Phd2SingleFrameResult(
                destinationPath,
                UsedLoopSaveFallback: true,
                RequestedParametersApplied: false,
                DateTimeOffset.UtcNow,
                VerifiedExposureMilliseconds: verifiedExposureMilliseconds,
                AutomaticRetryAllowed: false);
            UpdateSnapshot(current => current with { LastSingleFrame = result });
            return result;
        }
        finally
        {
            // This method deliberately does not stop the caller-owned loop,
            // including on timeout/cancellation. The caller performs one
            // checked stop for the whole bounded sequence.
            operationGate.Release();
        }
    }

    public async Task<Phd2Point> SelectGuideStarAsync(
        Phd2Point approximatePosition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approximatePosition);
        if (!double.IsFinite(approximatePosition.X) || !double.IsFinite(approximatePosition.Y) ||
            approximatePosition.X < 0 || approximatePosition.Y < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(approximatePosition),
                "Guide-star coordinates must be finite, non-negative image coordinates.");
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAutomationPaused();
            await InvokeAsync(
                    "set_lock_position",
                    new
                    {
                        x = approximatePosition.X,
                        y = approximatePosition.Y,
                        exact = false,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var result = await InvokeAsync("get_lock_position", parameters: null, cancellationToken).ConfigureAwait(false);
            var selected = ParsePointArray(result)
                ?? throw new Phd2Exception("PHD2 accepted guide-star selection but returned no lock position.");
            var deltaX = selected.X - approximatePosition.X;
            var deltaY = selected.Y - approximatePosition.Y;
            var separation = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
            if (separation > options.GuideStarSelectionTolerancePixels)
            {
                throw new Phd2Exception(
                    $"PHD2 selected a guide star {separation:F1} pixels from the requested coordinate; " +
                    $"the limit is {options.GuideStarSelectionTolerancePixels:F1} pixels.");
            }

            UpdateSnapshot(current => current with
            {
                LockPosition = selected,
                SelectedStar = selected,
            });
            return selected;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<Phd2Point> FindGuideStarInRoiAsync(
        Phd2Rectangle searchRoi,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(searchRoi);
        if (searchRoi.X < 0 || searchRoi.Y < 0 ||
            searchRoi.Width <= 0 || searchRoi.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(searchRoi),
                "Guide-star search ROI must have a non-negative origin and positive dimensions.");
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAutomationPaused();
            var result = await InvokeAsync(
                    "find_star",
                    new
                    {
                        roi = new[]
                        {
                            searchRoi.X,
                            searchRoi.Y,
                            searchRoi.Width,
                            searchRoi.Height,
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var selected = ParsePointArray(result)
                ?? throw new Phd2Exception("PHD2 found no guide star in the bounded search ROI.");
            var maximumX = searchRoi.X + searchRoi.Width;
            var maximumY = searchRoi.Y + searchRoi.Height;
            if (selected.X < searchRoi.X || selected.X > maximumX ||
                selected.Y < searchRoi.Y || selected.Y > maximumY)
            {
                throw new Phd2Exception(
                    $"PHD2 returned guide star ({selected.X:F2}, {selected.Y:F2}) outside " +
                    $"the requested ROI [{searchRoi.X}, {searchRoi.Y}, {searchRoi.Width}, {searchRoi.Height}].");
            }

            UpdateSnapshot(current => current with
            {
                LockPosition = selected,
                SelectedStar = selected,
            });
            return selected;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<Phd2Point> FindGuideStarAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAutomationPaused();
            var result = await InvokeAsync(
                    "find_star",
                    parameters: null,
                    cancellationToken)
                .ConfigureAwait(false);
            var selected = ParsePointArray(result)
                ?? throw new Phd2Exception("PHD2 native automatic selection found no guide star.");
            UpdateSnapshot(current => current with
            {
                LockPosition = selected,
                SelectedStar = selected,
            });
            return selected;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<double> GetPixelScaleAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await InvokeAsync("get_pixel_scale", parameters: null, cancellationToken)
                .ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Number ||
                !result.TryGetDouble(out var pixelScale) ||
                !double.IsFinite(pixelScale) || pixelScale <= 0)
            {
                throw new Phd2Exception("PHD2 returned an invalid image scale.");
            }

            return pixelScale;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<Phd2LoopingStartResult> StartLoopingAndWaitForFreshFrameAsync(
        Phd2LoopingStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FreshFrameTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Fresh LoopingExposures timeout must be positive.");
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAutomationPaused();
            var initial = await GetAppStateAsync(cancellationToken).ConfigureAwait(false);
            if (initial is not Phd2AppState.Stopped and not Phd2AppState.Selected)
            {
                throw new Phd2CaptureException(
                    $"PHD2 full-frame guide-star selection loop requires fresh Stopped or Selected state; " +
                    $"current state is {initial}. No loop or stop command was sent.");
            }

            var baseline = Snapshot;
            using var frameWaiter = RegisterEventWaiter(message =>
                message.Name == "LoopingExposures" &&
                message.Sequence > baseline.EventSequence &&
                GetOptionalInt64(message.Payload, "Frame") is >= 1);
            await InvokeAsync("loop", parameters: null, cancellationToken).ConfigureAwait(false);
            var frameEvent = await WaitForEventAsync(
                    frameWaiter,
                    "fresh full-frame looping exposure",
                    request.FreshFrameTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            var frame = GetOptionalInt64(frameEvent.Payload, "Frame")
                ?? throw new Phd2CaptureException("Fresh PHD2 LoopingExposures event omitted its frame number.");
            var attested = Snapshot;
            if (!attested.IsConnected || attested.AutomationPaused ||
                attested.ConnectionEpoch != baseline.ConnectionEpoch ||
                attested.AppState != Phd2AppState.Looping ||
                attested.EventSequence < frameEvent.Sequence)
            {
                throw new Phd2CaptureException(
                    "PHD2 did not remain in the same connected Looping epoch after the fresh full-frame event. " +
                    "The caller must perform checked cleanup; this method never sends stop_capture implicitly.");
            }

            return new Phd2LoopingStartResult(
                initial,
                frame,
                frameEvent.Sequence,
                frameEvent.ReceivedUtc,
                DateTimeOffset.UtcNow,
                attested.ConnectionEpoch,
                attested.GuideEpoch,
                LoopCommandSent: true,
                StopCommandSent: false,
                ExposureChanged: false,
                LeavesLoopingForGuideTakeover: true,
                AutomaticRetryAllowed: false);
        }
        finally
        {
            // Deliberately no BestEffortStopCaptureAsync here. A successfully
            // started loop is the selection surface that guide must take over;
            // an ambiguous failure is reconciled by the caller's checked
            // StopCaptureAndConfirmAsync cleanup, never by an automatic retry.
            operationGate.Release();
        }
    }

    public async Task<Phd2ExposureSelectionResult> SetExposureAndVerifyAsync(
        int exposureMilliseconds,
        CancellationToken cancellationToken)
    {
        if (exposureMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(exposureMilliseconds));
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAutomationPaused();
            var state = await GetAppStateAsync(cancellationToken).ConfigureAwait(false);
            if (state is not Phd2AppState.Stopped and not Phd2AppState.Selected)
                throw new Phd2CaptureException($"PHD2 exposure selection requires Stopped or Selected state; current state is {state}. No exposure command was sent.");
            try
            {
                await InvokeAsync("set_exposure", new[] { exposureMilliseconds }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is Phd2CommandTimeoutException or Phd2DisconnectedException or OperationCanceledException)
            {
                throw new Phd2Exception(
                    "The one allowed PHD2 set_exposure response was ambiguous. The command was not retried; reconnect/readback or checked stop is required.",
                    ex);
            }
            var readback = await InvokeAsync("get_exposure", parameters: null, cancellationToken).ConfigureAwait(false);
            if (readback.ValueKind != JsonValueKind.Number || !readback.TryGetInt32(out var verified) || verified <= 0)
                throw new Phd2Exception("PHD2 get_exposure returned a missing, non-integer, or non-positive value.");
            if (verified != exposureMilliseconds)
                throw new Phd2Exception($"PHD2 exposure readback {verified}ms does not match requested commissioned {exposureMilliseconds}ms.");
            return new Phd2ExposureSelectionResult(
                exposureMilliseconds,
                verified,
                state,
                DateTimeOffset.UtcNow,
                AutomaticRetryAllowed: false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<Phd2GuidingFrameResult> SaveCurrentGuidingFrameAsync(
        Phd2GuidingFrameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateGuidingFrameRequest(request);

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAutomationPaused();
            var destinationPath = Path.GetFullPath(request.DestinationPath);
            var appState = await GetAppStateAsync(cancellationToken).ConfigureAwait(false);
            if (appState != Phd2AppState.Guiding)
            {
                throw new Phd2CaptureException(
                    $"Fresh in-session G3 evidence requires PHD2 Guiding state; current state is {appState}. " +
                    "No capture, loop, exposure, or guiding command was sent.");
            }

            var baseline = Snapshot;
            var startingFrame = baseline.LastGuideStep?.Frame;
            using var frameWaiter = RegisterEventWaiter(message =>
            {
                if (message.Name != "GuideStep" || message.Sequence <= baseline.EventSequence)
                {
                    return false;
                }

                var candidate = ParseGuideStep(message.Payload);
                return candidate.Frame.HasValue &&
                    (!startingFrame.HasValue || candidate.Frame.Value > startingFrame.Value);
            });
            var guideStepEvent = await WaitForEventAsync(
                    frameWaiter,
                    "fresh guiding-frame evidence",
                    request.FreshGuideStepTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            var guideStep = ParseGuideStep(guideStepEvent.Payload);
            var guideFrame = guideStep.Frame
                ?? throw new Phd2CaptureException("Fresh PHD2 GuideStep omitted its frame number.");

            EnsureSameGuidingEpoch(baseline, "before save_image");
            var saveResult = await InvokeAsync("save_image", parameters: null, cancellationToken)
                .ConfigureAwait(false);
            var sourcePath = GetRequiredString(saveResult, "filename");
            var sha256 = await CopySavedImageToImmutableEvidenceAsync(
                    sourcePath,
                    destinationPath,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureSameGuidingEpoch(baseline, "after immutable evidence copy");

            return new Phd2GuidingFrameResult(
                destinationPath,
                sha256,
                guideFrame,
                guideStepEvent.Sequence,
                guideStepEvent.ReceivedUtc,
                DateTimeOffset.UtcNow,
                GuidingWasInterrupted: false,
                ExposureChanged: false,
                CaptureLoopStarted: false,
                AutomaticRetryAllowed: false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<Phd2Point?> GetLockPositionAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetLockPositionCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<Phd2ExactLockPositionResult> SetExactLockPositionAsync(
        Phd2ExactLockPositionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateExactLockPositionRequest(request);

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfAutomationPaused();
            var appState = await GetAppStateAsync(cancellationToken).ConfigureAwait(false);
            if (appState != Phd2AppState.Guiding)
            {
                throw new Phd2Exception(
                    $"Exact runtime lock-position shifts require PHD2 Guiding state; current state is {appState}.");
            }

            var lockShiftEnabledResult = await InvokeAsync(
                    "get_lock_shift_enabled",
                    parameters: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (lockShiftEnabledResult.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new Phd2Exception("PHD2 get_lock_shift_enabled returned a non-boolean value.");
            }
            if (lockShiftEnabledResult.GetBoolean())
            {
                throw new Phd2Exception(
                    "Exact staged slit placement is blocked while PHD2 continuous lock shifting is enabled.");
            }

            var before = await GetLockPositionCoreAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new Phd2Exception("PHD2 has no current runtime lock position.");
            var preconditionError = Distance(before, request.ExpectedCurrentPosition);
            if (preconditionError > request.MaximumExpectedCurrentErrorPixels)
            {
                throw new Phd2Exception(
                    $"PHD2 runtime lock-position precondition changed by {preconditionError:F3} pixels; " +
                    $"the limit is {request.MaximumExpectedCurrentErrorPixels:F3} pixels. No set was sent.");
            }

            var step = Distance(before, request.DesiredPosition);
            if (step > request.MaximumStepPixels)
            {
                throw new Phd2Exception(
                    $"Exact runtime lock-position step {step:F3} pixels exceeds the " +
                    $"{request.MaximumStepPixels:F3}-pixel bound. No set was sent.");
            }

            // Deliberately one mutation and no automatic retry. If the response
            // is ambiguous, the caller must reconcile through GetLockPositionAsync
            // and the durable staged-motion ledger before deciding recovery.
            // Invalidate any previous settle before crossing the mutation
            // boundary even if PHD2 does not emit LockPositionSet.
            UpdateSnapshot(InvalidateSettle);
            try
            {
                await InvokeAsync(
                        "set_lock_position",
                        new
                        {
                            x = request.DesiredPosition.X,
                            y = request.DesiredPosition.Y,
                            exact = true,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Phd2CommandTimeoutException or Phd2DisconnectedException or OperationCanceledException)
            {
                throw new Phd2LockPositionReconciliationRequiredException(
                    "The exact lock-position request has an ambiguous transport outcome. Do not resend it; " +
                    "reconcile with a fresh get_lock_position and the durable stage ledger.",
                    before,
                    request.DesiredPosition,
                    observed: null,
                    mutationResponseReceived: false,
                    ex);
            }

            Phd2Point? verified;
            try
            {
                verified = await GetLockPositionCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Phd2CommandTimeoutException or Phd2DisconnectedException or OperationCanceledException)
            {
                throw new Phd2LockPositionReconciliationRequiredException(
                    "PHD2 acknowledged the exact lock-position request, but fresh verification failed. " +
                    "Do not resend it; reconcile with a fresh get_lock_position and the durable stage ledger.",
                    before,
                    request.DesiredPosition,
                    observed: null,
                    mutationResponseReceived: true,
                    ex);
            }
            if (verified is null)
            {
                throw new Phd2LockPositionReconciliationRequiredException(
                    "PHD2 acknowledged the exact lock-position request but returned no lock position. " +
                    "Do not resend it; reconcile before recovery.",
                    before,
                    request.DesiredPosition,
                    observed: null,
                    mutationResponseReceived: true,
                    new Phd2Exception("get_lock_position returned null"));
            }
            var verificationError = Distance(verified, request.DesiredPosition);
            if (verificationError > request.MaximumVerificationErrorPixels)
            {
                throw new Phd2LockPositionReconciliationRequiredException(
                    $"PHD2 exact runtime lock-position verification error is {verificationError:F3} pixels; " +
                    $"the limit is {request.MaximumVerificationErrorPixels:F3} pixels. " +
                    "Do not resend; reconcile the observed position through the durable stage ledger.",
                    before,
                    request.DesiredPosition,
                    verified,
                    mutationResponseReceived: true,
                    new Phd2Exception("exact lock-position verification mismatch"));
            }

            return new Phd2ExactLockPositionResult(
                before,
                request.DesiredPosition,
                verified,
                step,
                verificationError,
                DateTimeOffset.UtcNow,
                Exact: true,
                RegistryProfileMutated: false,
                AutomaticRetryAllowed: false,
                PhysicalGuideSettled: false,
                RequiresGuideAndSettle: true);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task<Phd2SettleResult> GuideAndSettleAsync(
        Phd2SettleCriteria criteria,
        bool forceRecalibration,
        CancellationToken cancellationToken) =>
        GuideAndSettleAsync(criteria, forceRecalibration, selectionRoi: null, cancellationToken);

    /// <summary>
    /// Starts or re-settles guiding while constraining PHD2's documented
    /// fallback auto-selection to an already morphology-qualified region.
    /// The ROI is relevant only when PHD2 decides no star is selected; when it
    /// is already guiding, PHD2 simply begins another settle period.
    /// </summary>
    public async Task<Phd2SettleResult> GuideAndSettleAsync(
        Phd2SettleCriteria criteria,
        bool forceRecalibration,
        Phd2Rectangle? selectionRoi,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ValidateSettleCriteria(criteria);
        if (selectionRoi is not null &&
            (selectionRoi.X < 0 || selectionRoi.Y < 0 ||
             selectionRoi.Width <= 0 || selectionRoi.Height <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectionRoi),
                "Guide-star selection ROI must have a non-negative origin and positive dimensions.");
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        long? operationId = null;
        var operationCompleted = false;
        try
        {
            ThrowIfAutomationPaused();
            if (forceRecalibration)
            {
                await EnsureForcedRecalibrationPrerequisitesAsync(cancellationToken).ConfigureAwait(false);
                UpdateSnapshot(current => current with { CalibrationValidation = null });
            }
            else
            {
                EnsureCalibrationApprovedForGuiding();
                _ = await GetProfileAsync(cancellationToken).ConfigureAwait(false);
                EnsureCalibrationApprovedForGuiding();
            }

            var localOperationId = Interlocked.Increment(ref nextSettleOperationId);
            operationId = localOperationId;
            using var waiter = RegisterEventWaiter(message =>
                message.Name == "SettleDone" &&
                Snapshot.LastSettleOperationId == localOperationId);
            using var calibrationWaiter = forceRecalibration
                ? RegisterEventWaiter(message =>
                    message.Name is "CalibrationComplete" or "CalibrationFailed" &&
                    Snapshot.PendingSettleOperationId == localOperationId &&
                    Snapshot.PendingCalibrationTerminalSequence == message.Sequence)
                : null;
            UpdateSnapshot(current =>
            {
                var next = InvalidateSettle(current);
                return next with
                {
                    PendingSettleOperationId = localOperationId,
                    PendingSettleConnectionEpoch = next.ConnectionEpoch,
                    PendingSettleGuideEpoch = next.GuideEpoch,
                    PendingSettleArmedAfterSequence = next.EventSequence,
                    PendingSettleBeginSequence = null,
                    PendingSettleCommandAccepted = false,
                    PendingTakeoverLoopStopAllowed = current.AppState == Phd2AppState.Looping,
                    PendingLateLoopFrameAllowed = current.AppState == Phd2AppState.Looping,
                    PendingForceRecalibration = forceRecalibration,
                    PendingCalibrationStartSequence = null,
                    PendingCalibrationTerminalSequence = null,
                };
            });
            ThrowIfAutomationPaused();
            if (Snapshot.PendingSettleOperationId != localOperationId)
            {
                throw new Phd2Exception(
                    "PHD2 guide/settle operation lost its local epoch before the guide command was sent.");
            }
            var guideParameters = new Dictionary<string, object?>
            {
                ["settle"] = new
                {
                    pixels = criteria.Pixels,
                    time = criteria.StableTimeSeconds,
                    timeout = criteria.TimeoutSeconds,
                },
                ["recalibrate"] = forceRecalibration,
            };
            if (selectionRoi is not null)
            {
                guideParameters["roi"] = new[]
                {
                    selectionRoi.X,
                    selectionRoi.Y,
                    selectionRoi.Width,
                    selectionRoi.Height,
                };
            }

            await InvokeAsync(
                    "guide",
                    guideParameters,
                    cancellationToken)
                .ConfigureAwait(false);
            MarkGuideCommandAccepted(localOperationId);

            var eventTimeout = TimeSpan.FromSeconds(criteria.TimeoutSeconds) + options.EventTimeoutMargin;
            Phd2EventMessage? calibrationTerminal = null;
            if (calibrationWaiter is not null)
            {
                calibrationTerminal = await WaitForEventAsync(
                        calibrationWaiter,
                        "forced recalibration",
                        eventTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var settledEvent = await WaitForEventAsync(waiter, "guide settle", eventTimeout, cancellationToken)
                .ConfigureAwait(false);
            var eventResult = ParseSettleResult(settledEvent);
            var attested = Snapshot;
            if (attested.LastSettle is null ||
                attested.LastSettleOperationId != localOperationId ||
                !attested.LastSettleCommandAccepted ||
                attested.LastSettleConnectionEpoch != attested.ConnectionEpoch ||
                attested.LastSettleGuideEpoch != attested.GuideEpoch ||
                attested.LastSettle != eventResult)
            {
                throw new Phd2Exception(
                    "PHD2 SettleDone was not accepted as a current connection/guide-epoch attestation.");
            }
            var result = attested.LastSettle;
            if (forceRecalibration)
            {
                UpdateSnapshot(current => current with { CalibrationValidation = null });
            }
            if (calibrationTerminal?.Name == "CalibrationFailed")
            {
                var reason = GetOptionalString(calibrationTerminal.Payload, "Reason")
                    ?? "PHD2 reported an unspecified calibration failure.";
                throw new Phd2CalibrationRejectedException($"PHD2 forced recalibration failed: {reason}");
            }

            operationCompleted = true;
            return result;
        }
        catch (OperationCanceledException)
        {
            if (operationId.HasValue)
            {
                AbortSettleOperation(operationId.Value);
            }
            await BestEffortStopCaptureAsync().ConfigureAwait(false);
            throw;
        }
        catch (Phd2CommandTimeoutException)
        {
            if (operationId.HasValue)
            {
                AbortSettleOperation(operationId.Value);
            }
            await BestEffortStopCaptureAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            if (operationId.HasValue)
            {
                AbortSettleOperation(operationId.Value);
            }
            throw;
        }
        finally
        {
            if (!operationCompleted && operationId.HasValue)
            {
                AbortSettleOperation(operationId.Value);
            }
            operationGate.Release();
        }
    }

    public async Task<Phd2StopCaptureResult> StopCaptureAndConfirmAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        UpdateSnapshot(InvalidateSettle);
        var initial = await GetAppStateAsync(cancellationToken).ConfigureAwait(false);
        if (IsIdle(initial))
        {
            return new Phd2StopCaptureResult(
                initial,
                initial,
                StopCommandSent: false,
                ConfirmedIdle: true,
                DateTimeOffset.UtcNow);
        }

        await InvokeAsync("stop_capture", parameters: null, cancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + options.StopConfirmationTimeout;
        var final = Phd2AppState.Unknown;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            final = await GetAppStateAsync(cancellationToken).ConfigureAwait(false);
            if (IsIdle(final))
            {
                UpdateSnapshot(current => InvalidateSettle(current with
                {
                    AppState = final,
                    Phd2Paused = false,
                    SettleProgress = null,
                }));
                return new Phd2StopCaptureResult(
                    initial,
                    final,
                    StopCommandSent: true,
                    ConfirmedIdle: true,
                    DateTimeOffset.UtcNow);
            }
            await Task.Delay(options.StatePollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new Phd2CommandTimeoutException(
            $"stop_capture idle confirmation (last state {final})",
            options.StopConfirmationTimeout);
    }

    public async Task<Phd2StopCaptureResult> PauseAutomationAndStopCaptureAsync(
        CancellationToken cancellationToken)
    {
        PauseAutomation();
        return await StopCaptureAndConfirmAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopGuidingAsync(CancellationToken cancellationToken)
    {
        _ = await StopCaptureAndConfirmAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        connectionGate.Dispose();
        writeGate.Dispose();
        operationGate.Dispose();
    }

    private async Task EnsureForcedRecalibrationPrerequisitesAsync(CancellationToken cancellationToken)
    {
        Phd2IdentityValidation approvedIdentity;
        Phd2Point selectedStar;
        lock (stateGate)
        {
            approvedIdentity = approvedIdentityValidation
                ?? throw new Phd2Exception(
                    "Forced PHD2 recalibration requires a successful identity validation from the coordinator.");
            selectedStar = snapshot.SelectedStar
                ?? throw new Phd2Exception(
                    "Forced PHD2 recalibration requires an explicitly selected guide star.");
        }

        var currentProfile = await GetProfileAsync(cancellationToken).ConfigureAwait(false);
        if (currentProfile != approvedIdentity.Profile)
        {
            throw new Phd2Exception(
                $"Forced PHD2 recalibration is blocked because profile {currentProfile.Id}/'{currentProfile.Name}' " +
                $"does not match the approved profile {approvedIdentity.Profile.Id}/'{approvedIdentity.Profile.Name}'.");
        }

        var currentEquipment = await GetCurrentEquipmentAsync(cancellationToken).ConfigureAwait(false);
        if (currentEquipment != approvedIdentity.Equipment)
        {
            throw new Phd2Exception(
                "Forced PHD2 recalibration is blocked because the current equipment no longer matches " +
                "the coordinator-approved identity.");
        }

        var lockPositionResult = await InvokeAsync("get_lock_position", parameters: null, cancellationToken)
            .ConfigureAwait(false);
        var lockPosition = ParsePointArray(lockPositionResult)
            ?? throw new Phd2Exception(
                "Forced PHD2 recalibration is blocked because PHD2 has no selected guide-star lock position.");
        var deltaX = lockPosition.X - selectedStar.X;
        var deltaY = lockPosition.Y - selectedStar.Y;
        var separation = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        if (separation > options.GuideStarSelectionTolerancePixels)
        {
            throw new Phd2Exception(
                $"Forced PHD2 recalibration is blocked because the current lock position moved {separation:F1} " +
                $"pixels from the explicitly selected star; the limit is " +
                $"{options.GuideStarSelectionTolerancePixels:F1} pixels.");
        }

        UpdateSnapshot(current => current with { LockPosition = lockPosition });
    }

    private async Task<Phd2SingleFrameResult> CaptureUsingLoopSaveAsync(
        Phd2SingleFrameRequest request,
        CancellationToken cancellationToken)
    {
        var appState = await GetAppStateAsync(cancellationToken).ConfigureAwait(false);
        if (appState is not Phd2AppState.Stopped and not Phd2AppState.Selected)
        {
            throw new Phd2CaptureException(
                $"PHD2 full-frame acquisition requires an idle Stopped or Selected state; current state is {appState}. " +
                "The existing capture, calibration, or guiding session was left untouched.");
        }

        var priorExposureResult = await InvokeAsync(
                "get_exposure",
                parameters: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (priorExposureResult.ValueKind != JsonValueKind.Number ||
            !priorExposureResult.TryGetInt32(out var priorExposureMilliseconds) ||
            priorExposureMilliseconds <= 0)
        {
            throw new Phd2CaptureException(
                "PHD2 did not return a valid pre-mutation exposure. No exposure or loop command was sent.");
        }

        // One exposure mutation only.  Its response and a fresh readback must
        // both be unambiguous before a selection frame is allowed to start.
        // The caller must never retry this method after an ambiguous outcome.
        try
        {
            await InvokeAsync("set_exposure", new[] { request.ExposureMs }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is Phd2CommandTimeoutException or Phd2DisconnectedException or OperationCanceledException)
        {
            throw new Phd2Exception(
                "The one allowed PHD2 selection-frame set_exposure response was ambiguous. " +
                "No loop was started and the command was not retried.",
                ex);
        }
        var exposureReadback = await InvokeAsync("get_exposure", parameters: null, cancellationToken).ConfigureAwait(false);
        if (exposureReadback.ValueKind != JsonValueKind.Number ||
            !exposureReadback.TryGetInt32(out var verifiedExposureMilliseconds) ||
            verifiedExposureMilliseconds != request.ExposureMs)
        {
            throw new Phd2CaptureException(
                $"PHD2 exposure readback did not exactly match the commissioned {request.ExposureMs}ms selection exposure. No loop was started.");
        }

        // PHD2/camera pipelines can publish one already-buffered image after a
        // set_exposure transition.  When the value changed, deliberately
        // discard the first post-loop event and save only the following frame.
        // This is especially important for the 10 ms bright-target route,
        // where a stale 50 ms frame can saturate a completely different halo.
        var requiredLoopFrames = priorExposureMilliseconds == request.ExposureMs ? 1 : 2;
        var observedLoopFrames = 0;
        var frameBaselineSequence = Snapshot.EventSequence;
        using var frameWaiter = RegisterEventWaiter(message =>
            message.Name == "LoopingExposures" &&
            message.Sequence > frameBaselineSequence &&
            Interlocked.Increment(ref observedLoopFrames) >= requiredLoopFrames);
        var loopingStarted = false;
        string sourcePath;
        try
        {
            await InvokeAsync("loop", parameters: null, cancellationToken).ConfigureAwait(false);
            loopingStarted = true;
            var frameTimeout = ExposureBoundLoopingFrameTimeout(
                request.ExposureMs,
                requiredLoopFrames);
            await WaitForEventAsync(
                    frameWaiter,
                    "exposure-bound looping-frame capture",
                    frameTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            using var stoppedWaiter = RegisterEventWaiter(message => message.Name == "LoopingExposuresStopped");
            await InvokeAsync("stop_capture", parameters: null, cancellationToken).ConfigureAwait(false);
            await WaitForEventAsync(
                    stoppedWaiter,
                    "looping-frame stop",
                    options.EventTimeoutMargin,
                    cancellationToken)
                .ConfigureAwait(false);
            loopingStarted = false;

            var saveResult = await InvokeAsync("save_image", parameters: null, cancellationToken).ConfigureAwait(false);
            sourcePath = GetRequiredString(saveResult, "filename");
        }
        finally
        {
            if (loopingStarted)
            {
                await BestEffortStopCaptureAsync().ConfigureAwait(false);
            }
        }

        await CopySavedImageToImmutableEvidenceAsync(sourcePath, request.DestinationPath, cancellationToken)
            .ConfigureAwait(false);
        var result = new Phd2SingleFrameResult(
            request.DestinationPath,
            UsedLoopSaveFallback: true,
            RequestedParametersApplied: false,
            DateTimeOffset.UtcNow,
            VerifiedExposureMilliseconds: verifiedExposureMilliseconds,
            AutomaticRetryAllowed: false);
        UpdateSnapshot(current => current with { LastSingleFrame = result });
        return result;
    }

    private async Task<Phd2AppState> GetAppStateAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync("get_app_state", parameters: null, cancellationToken).ConfigureAwait(false);
        var appState = result.ValueKind == JsonValueKind.String
            ? ParseAppState(result.GetString())
            : Phd2AppState.Unknown;
        UpdateSnapshot(current => ApplyObservedAppState(current, appState));
        return appState;
    }

    private TimeSpan ExposureBoundLoopingFrameTimeout(int exposureMilliseconds, int frameCount = 1)
    {
        if (frameCount < 1) throw new ArgumentOutOfRangeException(nameof(frameCount));
        var exposureBound = TimeSpan.FromMilliseconds(exposureMilliseconds) + options.EventTimeoutMargin;
        var perFrame = exposureBound >= options.MinimumLoopingFrameEventTimeout
            ? exposureBound
            : options.MinimumLoopingFrameEventTimeout;
        return TimeSpan.FromTicks(checked(perFrame.Ticks * frameCount));
    }

    private void EnsureSameGuidingEpoch(Phd2StateSnapshot baseline, string phase)
    {
        var current = Snapshot;
        if (!current.IsConnected || current.AppState != Phd2AppState.Guiding ||
            current.ConnectionEpoch != baseline.ConnectionEpoch || current.GuideEpoch != baseline.GuideEpoch)
        {
            throw new Phd2CaptureException(
                $"PHD2 guiding session changed {phase}; the saved frame cannot attest the requested guide epoch.");
        }
    }

    private async Task<string> CopySavedImageToImmutableEvidenceAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new Phd2CaptureException($"PHD2 save_image returned a non-absolute filename: '{sourcePath}'.");
        }

        sourcePath = Path.GetFullPath(sourcePath);
        destinationPath = Path.GetFullPath(destinationPath);
        await WaitForFileReadyAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        try
        {
            if (PathEquals(sourcePath, destinationPath))
            {
                throw new Phd2CaptureException("PHD2 save_image unexpectedly returned the immutable evidence path itself.");
            }

            File.Copy(sourcePath, destinationPath, overwrite: false);
        }
        catch (Phd2CaptureException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new Phd2CaptureException(
                $"Could not copy PHD2 saved image '{sourcePath}' to '{destinationPath}': {ex.Message}");
        }

        await WaitForFileReadyAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        await using var stream = new FileStream(
            destinationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        TryDeleteSavedImage(sourcePath);
        return sha256;
    }

    private static void TryDeleteSavedImage(string sourcePath)
    {
        try
        {
            File.Delete(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The immutable evidence copy is complete. A PHD2 temporary-file cleanup
            // failure must not turn a successful acquisition into a false failure.
        }
    }

    private async Task<JsonElement> InvokeAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ThrowIfDisposed();
        StreamWriter localWriter;
        lock (transportGate)
        {
            localWriter = writer ?? throw new Phd2DisconnectedException("PHD2 event server is not connected.");
        }

        var id = Interlocked.Increment(ref nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingRequests.TryAdd(id, completion))
        {
            throw new InvalidOperationException($"Duplicate PHD2 JSON-RPC request id {id}.");
        }

        var request = JsonSerializer.Serialize(
            new JsonRpcRequest("2.0", method, parameters, id),
            SerializerOptions);

        try
        {
            await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await localWriter.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new Phd2DisconnectedException("Failed to write a request to the PHD2 event server.", ex);
            }
            finally
            {
                writeGate.Release();
            }

            var effectiveTimeout = timeout ?? options.CommandTimeout;
            try
            {
                return await completion.Task.WaitAsync(effectiveTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new Phd2CommandTimeoutException(method, effectiveTimeout);
            }
        }
        finally
        {
            pendingRequests.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(
        StreamReader localReader,
        Task startTask,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await startTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await localReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    throw new Phd2DisconnectedException("PHD2 closed the event-server connection.");
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    ProcessIncoming(document.RootElement);
                }
                catch (JsonException ex)
                {
                    UpdateSnapshot(current => current with
                    {
                        LastProtocolError = $"Malformed PHD2 JSON line: {ex.Message}",
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (failure is not null)
            {
                var disconnected = failure as Phd2DisconnectedException ??
                    new Phd2DisconnectedException("PHD2 event-server read loop failed.", failure);
                DetachFailedTransport(localReader);
                CompleteOutstanding(disconnected);
                MarkDisconnected(disconnected.Message);
            }
        }
    }

    private void ProcessIncoming(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                ProcessIncoming(item);
            }

            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            UpdateSnapshot(current => current with
            {
                LastProtocolError = "PHD2 sent a JSON value that was neither an object nor a batch array.",
            });
            return;
        }

        if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
        {
            ProcessResponse(id, root);
            return;
        }

        if (root.TryGetProperty("Event", out var eventElement) && eventElement.ValueKind == JsonValueKind.String)
        {
            ProcessEvent(eventElement.GetString()!, root.Clone());
        }
    }

    private void ProcessResponse(long id, JsonElement response)
    {
        if (!pendingRequests.TryGetValue(id, out var completion))
        {
            return;
        }

        if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode)
                ? parsedCode
                : -32603;
            var message = GetOptionalString(error, "message") ?? "Unknown PHD2 JSON-RPC error.";
            var data = error.TryGetProperty("data", out var dataElement) ? dataElement.GetRawText() : null;
            completion.TrySetException(new Phd2RpcException(code, message, data));
            return;
        }

        if (response.TryGetProperty("result", out var result))
        {
            completion.TrySetResult(result.Clone());
            return;
        }

        completion.TrySetException(new Phd2Exception($"PHD2 response {id} contained neither result nor error."));
    }

    private void ProcessEvent(string name, JsonElement payload)
    {
        var receivedUtc = DateTimeOffset.UtcNow;
        var sequence = Interlocked.Increment(ref nextEventSequence);
        var message = new Phd2EventMessage(name, sequence, receivedUtc, payload);

        UpdateSnapshot(current =>
        {
            if (message.Name == "ConfigurationChange")
            {
                approvedIdentityValidation = null;
            }

            return ApplyEvent(current, message);
        });
        CompleteMatchingEventWaiters(message);
        InvokeEventHandlersSafely(EventReceived, message);
    }

    private Phd2StateSnapshot ApplyEvent(Phd2StateSnapshot current, Phd2EventMessage message)
    {
        var next = current with
        {
            EventSequence = message.Sequence,
            LastEventUtc = message.ReceivedUtc,
        };

        return message.Name switch
        {
            "Version" => next with
            {
                PhdVersion = BuildVersion(message.Payload),
            },
            "AppState" => ApplyObservedAppState(
                next,
                ParseAppState(GetOptionalString(message.Payload, "State"))),
            "LockPositionSet" when IsPendingGuideStartLockConfirmation(next, message.Payload) => next with
            {
                LockPosition = ParsePointObject(message.Payload),
            },
            "LockPositionSet" => InvalidateSettle(next with
            {
                LockPosition = ParsePointObject(message.Payload),
            }),
            "LockPositionLost" => InvalidateSettle(next with
            {
                LockPosition = null,
                AppState = Phd2AppState.LostLock,
            }),
            "GuidingDithered" => InvalidateSettle(next),
            "LockPositionShiftLimitReached" => InvalidateSettle(next with
            {
                LastAlert = "PHD2 lock-position shift limit reached.",
            }),
            "CalibrationDataFlipped" => InvalidateSettle(next with
            {
                CalibrationValidation = null,
            }),
            // PHD2 can repeat StarSelected after accepting the local guide
            // RPC but before StartGuiding/SettleBegin.  If it still names the
            // already accepted, morphology-qualified lock neighbourhood, it
            // is guide-takeover confirmation rather than an external reselect.
            "StarSelected" when IsPendingGuideStartLockConfirmation(next, message.Payload) => next with
            {
                SelectedStar = ParsePointObject(message.Payload),
            },
            "StarSelected" => InvalidateSettle(next with
            {
                SelectedStar = ParsePointObject(message.Payload),
            }),
            "StartGuiding" => BeginGuideEpoch(next),
            "GuidingStopped" => InvalidateSettle(next with
            {
                AppState = Phd2AppState.Stopped,
                Phd2Paused = false,
            }),
            "Paused" => InvalidateSettle(next with
            {
                AppState = Phd2AppState.Paused,
                Phd2Paused = true,
            }),
            "Resumed" => InvalidateSettle(next with
            {
                AppState = Phd2AppState.Guiding,
                Phd2Paused = false,
            }),
            // PHD2 may announce its exposure loop while a guide/settle RPC is
            // already pending.  In that interval this is transport progress,
            // not a competing lifecycle transition; erasing the pending epoch
            // would reject the matching SettleDone from the same RPC.
            "LoopingExposures" when IsPendingSettleOperationCurrent(next) &&
                !next.PendingSettleBeginSequence.HasValue => next with
                {
                    PendingLateLoopFrameAllowed = true,
                },
            "LoopingExposures" when IsPendingSettleOperationCurrent(next) &&
                next.PendingLateLoopFrameAllowed => next with
                {
                    PendingLateLoopFrameAllowed = false,
                },
            "LoopingExposures" when next.AppState == Phd2AppState.Looping => next,
            "LoopingExposures" => InvalidateSettle(next with
            {
                AppState = Phd2AppState.Looping,
            }),
            // When guide takes ownership of an intentionally running selection
            // loop, PHD2 may emit LoopingExposuresStopped before or after
            // StartGuiding. It is progress inside the already-pending guide RPC,
            // not an external stop that may erase the matching settle epoch.
            "LoopingExposuresStopped" when IsPendingSettleOperationCurrent(next) &&
                next.PendingTakeoverLoopStopAllowed => next with
                {
                    PendingTakeoverLoopStopAllowed = false,
                },
            "LoopingExposuresStopped" => InvalidateSettle(next with
            {
                AppState = Phd2AppState.Stopped,
            }),
            "SettleBegin" => ApplySettleBegin(next, message),
            "Settling" => ApplySettling(next, message.Payload),
            "SettleDone" => ApplySettleDone(next, message),
            "GuideStep" => ApplyGuideStep(next, message.Payload),
            // Thin cloud or momentary seeing can drop one centroid while the
            // same locally issued PHD2 settle operation remains active.  PHD2
            // is authoritative for whether that operation ultimately settles
            // or fails, so retain the pending epoch until its SettleDone.
            "StarLost" when IsPendingSettleOperationCurrent(next) => next with
            {
                AppState = Phd2AppState.LostLock,
                LastGuideStep = ParseGuideStep(message.Payload),
            },
            "StarLost" => InvalidateSettle(next with
            {
                AppState = Phd2AppState.LostLock,
                LastGuideStep = ParseGuideStep(message.Payload),
            }),
            "Alert" => next with
            {
                LastAlert = GetOptionalString(message.Payload, "Msg"),
            },
            "StartCalibration" => ApplyStartCalibration(next, message),
            "CalibrationComplete" or "CalibrationFailed" =>
                ApplyCalibrationTerminal(next, message),
            // PHD2 emits ConfigurationChange while persisting a freshly
            // completed calibration, including after StartGuiding/SettleBegin.
            // That does not begin a new guide epoch and must not erase the
            // pending settle attestation.  Calibration authority is still
            // invalidated and must be re-read after SettleDone.
            "ConfigurationChange" when IsPendingSettleOperationCurrent(next) => next with
            {
                CalibrationValidation = null,
            },
            "ConfigurationChange" => InvalidateSettle(next with
            {
                CalibrationValidation = null,
            }),
            _ => next,
        };
    }

    private static Phd2StateSnapshot BeginGuideEpoch(Phd2StateSnapshot current)
    {
        // PHD2 does not guarantee whether StartGuiding or SettleBegin is
        // delivered first for the same locally issued guide RPC.  A validated
        // SettleBegin already belongs to the current pending operation, so a
        // later StartGuiding is progress within that operation rather than a
        // competing guide lifecycle transition.  With no current pending
        // operation, StartGuiding still invalidates all prior settle evidence.
        var preservePending = IsPendingSettleOperationCurrent(current);
        var next = InvalidateSettle(current with
        {
            AppState = Phd2AppState.Guiding,
            Phd2Paused = false,
        });
        return preservePending ? RestorePendingSettle(current, next) : next;
    }

    private bool IsPendingGuideStartLockConfirmation(
        Phd2StateSnapshot current,
        JsonElement payload)
    {
        if (!IsPendingSettleOperationCurrent(current) ||
            current.PendingSettleBeginSequence.HasValue ||
            current.LockPosition is null)
        {
            return false;
        }

        // Non-exact selection is intentionally allowed to snap from the
        // requested search coordinate to a nearby stellar centroid.  PHD2
        // announces that accepted centroid once more as guide takes over the
        // selection loop, so use the same bounded tolerance as selection.
        var announced = ParsePointObject(payload);
        return announced is not null &&
               Distance(current.LockPosition, announced) <= options.GuideStarSelectionTolerancePixels;
    }

    private static Phd2StateSnapshot ApplySettleBegin(
        Phd2StateSnapshot current,
        Phd2EventMessage message)
    {
        if (!IsPendingSettleOperationCurrent(current) ||
            !current.PendingSettleArmedAfterSequence.HasValue ||
            message.Sequence <= current.PendingSettleArmedAfterSequence.Value ||
            current.PendingSettleBeginSequence.HasValue)
        {
            return InvalidateSettle(current with { SettleProgress = null });
        }

        return current with
        {
            SettleProgress = null,
            PendingSettleBeginSequence = message.Sequence,
        };
    }

    private static Phd2StateSnapshot ApplySettling(
        Phd2StateSnapshot current,
        JsonElement payload)
    {
        if (!IsPendingSettleOperationCurrent(current) ||
            !current.PendingSettleBeginSequence.HasValue)
        {
            return InvalidateSettle(current);
        }

        return current with
        {
            SettleProgress = ParseSettleProgress(payload),
            PendingLateLoopFrameAllowed = false,
        };
    }

    private static Phd2StateSnapshot ApplyGuideStep(
        Phd2StateSnapshot current,
        JsonElement payload)
    {
        var step = ParseGuideStep(payload);
        if (!IsPendingSettleOperationCurrent(current))
        {
            return ApplyObservedAppState(
                current with { LastGuideStep = step },
                Phd2AppState.Guiding);
        }

        if (current.AppState is Phd2AppState.Guiding or Phd2AppState.LostLock)
        {
            return current with
            {
                AppState = Phd2AppState.Guiding,
                LastGuideStep = step,
                PendingTakeoverLoopStopAllowed = false,
                PendingLateLoopFrameAllowed = false,
            };
        }

        var transitioned = InvalidateSettle(current with
        {
            AppState = Phd2AppState.Guiding,
            Phd2Paused = false,
            LastGuideStep = step,
        });
        return RestorePendingSettle(current, transitioned) with
        {
            LastGuideStep = step,
            PendingTakeoverLoopStopAllowed = false,
            PendingLateLoopFrameAllowed = false,
        };
    }

    private static Phd2StateSnapshot ApplyStartCalibration(
        Phd2StateSnapshot current,
        Phd2EventMessage message)
    {
        var expected = IsPendingSettleOperationCurrent(current) &&
            current.PendingForceRecalibration &&
            !current.PendingCalibrationStartSequence.HasValue &&
            !current.PendingSettleBeginSequence.HasValue &&
            current.PendingSettleArmedAfterSequence.HasValue &&
            message.Sequence > current.PendingSettleArmedAfterSequence.Value;
        var transitioned = InvalidateSettle(current with
        {
            AppState = Phd2AppState.Calibrating,
            CalibrationValidation = null,
        });
        if (!expected)
        {
            return transitioned;
        }

        return RestorePendingSettle(current, transitioned) with
        {
            PendingCalibrationStartSequence = message.Sequence,
            PendingCalibrationTerminalSequence = null,
            PendingTakeoverLoopStopAllowed = false,
            PendingLateLoopFrameAllowed = true,
        };
    }

    private static Phd2StateSnapshot ApplyCalibrationTerminal(
        Phd2StateSnapshot current,
        Phd2EventMessage message)
    {
        if (!IsPendingSettleOperationCurrent(current) ||
            !current.PendingForceRecalibration ||
            !current.PendingCalibrationStartSequence.HasValue ||
            current.PendingCalibrationTerminalSequence.HasValue ||
            message.Sequence <= current.PendingCalibrationStartSequence.Value)
        {
            return InvalidateSettle(current with { CalibrationValidation = null });
        }

        return current with
        {
            CalibrationValidation = null,
            PendingCalibrationTerminalSequence = message.Sequence,
        };
    }

    private static bool IsPendingSettleOperationCurrent(Phd2StateSnapshot current) =>
        current.PendingSettleOperationId.HasValue &&
        current.PendingSettleConnectionEpoch == current.ConnectionEpoch &&
        current.PendingSettleGuideEpoch == current.GuideEpoch;

    private static Phd2StateSnapshot RestorePendingSettle(
        Phd2StateSnapshot source,
        Phd2StateSnapshot destination) => destination with
    {
        PendingSettleOperationId = source.PendingSettleOperationId,
        PendingSettleConnectionEpoch = destination.ConnectionEpoch,
        PendingSettleGuideEpoch = destination.GuideEpoch,
        PendingSettleArmedAfterSequence = source.PendingSettleArmedAfterSequence,
        PendingSettleBeginSequence = source.PendingSettleBeginSequence,
        PendingSettleCommandAccepted = source.PendingSettleCommandAccepted,
        PendingTakeoverLoopStopAllowed = source.PendingTakeoverLoopStopAllowed,
        PendingLateLoopFrameAllowed = source.PendingLateLoopFrameAllowed,
        PendingForceRecalibration = source.PendingForceRecalibration,
        PendingCalibrationStartSequence = source.PendingCalibrationStartSequence,
        PendingCalibrationTerminalSequence = source.PendingCalibrationTerminalSequence,
    };

    private static Phd2StateSnapshot ApplyObservedAppState(
        Phd2StateSnapshot current,
        Phd2AppState appState)
    {
        if (current.AppState == appState)
        {
            return current with { AppState = appState };
        }
        return InvalidateSettle(current with
        {
            AppState = appState,
            Phd2Paused = appState == Phd2AppState.Paused,
        });
    }

    private static Phd2StateSnapshot ApplySettleDone(
        Phd2StateSnapshot current,
        Phd2EventMessage message)
    {
        if (!IsPendingSettleOperationCurrent(current))
        {
            return InvalidateSettle(current);
        }

        var result = ParseSettleResult(message);
        var successfulCompletionIsBound = result.Succeeded &&
            current.PendingSettleBeginSequence.HasValue &&
            message.Sequence > current.PendingSettleBeginSequence.Value;
        var failedCompletionIsBound = !result.Succeeded &&
            current.PendingSettleArmedAfterSequence.HasValue &&
            message.Sequence > current.PendingSettleArmedAfterSequence.Value;
        if ((!successfulCompletionIsBound && !failedCompletionIsBound) ||
            current.AutomationPaused ||
            current.Phd2Paused ||
            !current.IsConnected)
        {
            return InvalidateSettle(current);
        }

        return current with
        {
            AppState = result.Succeeded ? Phd2AppState.Guiding : current.AppState,
            LastSettle = result,
            SettleProgress = null,
            LastSettleOperationId = current.PendingSettleOperationId,
            LastSettleCommandAccepted = current.PendingSettleCommandAccepted,
            PendingSettleOperationId = null,
            PendingSettleConnectionEpoch = null,
            PendingSettleGuideEpoch = null,
            PendingSettleArmedAfterSequence = null,
            PendingSettleBeginSequence = null,
            PendingSettleCommandAccepted = false,
            PendingTakeoverLoopStopAllowed = false,
            PendingLateLoopFrameAllowed = false,
            PendingForceRecalibration = false,
            PendingCalibrationStartSequence = null,
            PendingCalibrationTerminalSequence = null,
            LastSettleConnectionEpoch = current.ConnectionEpoch,
            LastSettleGuideEpoch = current.GuideEpoch,
        };
    }

    private static Phd2StateSnapshot InvalidateSettle(Phd2StateSnapshot current) =>
        current with
        {
            GuideEpoch = checked(current.GuideEpoch + 1),
            LastSettle = null,
            SettleProgress = null,
            PendingSettleOperationId = null,
            PendingSettleConnectionEpoch = null,
            PendingSettleGuideEpoch = null,
            PendingSettleArmedAfterSequence = null,
            PendingSettleBeginSequence = null,
            PendingSettleCommandAccepted = false,
            PendingTakeoverLoopStopAllowed = false,
            PendingLateLoopFrameAllowed = false,
            PendingForceRecalibration = false,
            PendingCalibrationStartSequence = null,
            PendingCalibrationTerminalSequence = null,
            LastSettleOperationId = null,
            LastSettleCommandAccepted = false,
            LastSettleConnectionEpoch = null,
            LastSettleGuideEpoch = null,
        };

    private void MarkGuideCommandAccepted(long operationId)
    {
        UpdateSnapshot(current =>
        {
            if (current.PendingSettleOperationId == operationId)
            {
                return current with { PendingSettleCommandAccepted = true };
            }

            if (current.LastSettleOperationId == operationId)
            {
                return current with { LastSettleCommandAccepted = true };
            }

            return current;
        });
    }

    private void AbortSettleOperation(long operationId)
    {
        UpdateSnapshot(current =>
            current.PendingSettleOperationId == operationId ||
            current.LastSettleOperationId == operationId
                ? InvalidateSettle(current)
                : current);
    }

    private EventWaiterRegistration RegisterEventWaiter(Func<Phd2EventMessage, bool> predicate)
    {
        var id = Guid.NewGuid();
        var waiter = new EventWaiter(predicate);
        lock (eventWaiterGate)
        {
            eventWaiters.Add(id, waiter);
        }

        return new EventWaiterRegistration(this, id, waiter.Completion.Task);
    }

    private void RemoveEventWaiter(Guid id)
    {
        lock (eventWaiterGate)
        {
            eventWaiters.Remove(id);
        }
    }

    private void CompleteMatchingEventWaiters(Phd2EventMessage message)
    {
        List<EventWaiter> matching = [];
        lock (eventWaiterGate)
        {
            foreach (var pair in eventWaiters.ToArray())
            {
                bool isMatch;
                try
                {
                    isMatch = pair.Value.Predicate(message);
                }
                catch (Exception ex)
                {
                    pair.Value.Completion.TrySetException(ex);
                    eventWaiters.Remove(pair.Key);
                    continue;
                }

                if (isMatch)
                {
                    matching.Add(pair.Value);
                    eventWaiters.Remove(pair.Key);
                }
            }
        }

        foreach (var waiter in matching)
        {
            waiter.Completion.TrySetResult(message);
        }
    }

    private static async Task<Phd2EventMessage> WaitForEventAsync(
        EventWaiterRegistration waiter,
        string operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await waiter.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new Phd2CommandTimeoutException(operation, timeout);
        }
    }

    private async Task BestEffortStopCaptureAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(options.CommandTimeout);
            await InvokeAsync("stop_capture", parameters: null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Phd2Exception or OperationCanceledException or TimeoutException or ObjectDisposedException)
        {
        }
    }

    private async Task WaitForFileReadyAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + options.FileReadyTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 0)
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (stream.Length > 0)
                    {
                        return;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }

        throw new Phd2CaptureException($"PHD2 reported a saved image, but '{path}' did not become readable.");
    }

    private void CompleteOutstanding(Exception exception)
    {
        foreach (var request in pendingRequests.Values)
        {
            request.TrySetException(exception);
        }

        List<EventWaiter> waiters;
        lock (eventWaiterGate)
        {
            waiters = eventWaiters.Values.ToList();
            eventWaiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetException(exception);
        }
    }

    private void DetachFailedTransport(StreamReader failedReader)
    {
        TcpClient? failedClient;
        StreamWriter? failedWriter;
        CancellationTokenSource? failedCancellation;
        lock (transportGate)
        {
            if (!ReferenceEquals(reader, failedReader))
            {
                return;
            }

            failedClient = tcpClient;
            failedWriter = writer;
            failedCancellation = connectionCancellation;
            tcpClient = null;
            reader = null;
            writer = null;
            connectionCancellation = null;
            readerTask = null;
        }

        failedCancellation?.Cancel();
        failedWriter?.Dispose();
        failedReader.Dispose();
        failedClient?.Dispose();
        failedCancellation?.Dispose();
    }

    private void MarkDisconnected(string? error = null)
    {
        UpdateSnapshot(current =>
        {
            approvedIdentityValidation = null;
            return InvalidateSettle(current with
            {
                IsConnected = false,
                // Preserve a requested automation pause across transport
                // loss. Only ResumeAutomation may clear it.
                AutomationPaused = current.AutomationPaused,
                Phd2Paused = false,
                AppState = Phd2AppState.Unknown,
                CalibrationValidation = null,
                LastProtocolError = error ?? current.LastProtocolError,
            });
        });
    }

    private void UpdateSnapshot(Func<Phd2StateSnapshot, Phd2StateSnapshot> update)
    {
        Phd2StateSnapshot next;
        lock (stateGate)
        {
            next = update(snapshot);
            if (next == snapshot)
            {
                return;
            }

            snapshot = next;
        }

        InvokeEventHandlersSafely(SnapshotChanged, next);
    }

    private void ThrowIfAutomationPaused()
    {
        if (IsAutomationPaused)
        {
            throw new Phd2AutomationPausedException();
        }
    }

    private void EnsureCalibrationApprovedForGuiding()
    {
        var validation = Snapshot.CalibrationValidation;
        if (validation is null)
        {
            throw new Phd2CalibrationRejectedException(
                "PHD2 guiding is blocked until calibration has passed the sanity gate.");
        }

        if (!validation.IsValid)
        {
            throw new Phd2CalibrationRejectedException(
                $"PHD2 guiding is blocked because calibration status is {validation.Status}: " +
                string.Join("; ", validation.Failures.Concat(validation.IndeterminateReasons)),
                validation);
        }

        var validationAge = DateTimeOffset.UtcNow - validation.EvaluatedUtc;
        if (validationAge < TimeSpan.Zero || validationAge > options.CalibrationValidationTtl)
        {
            throw new Phd2CalibrationRejectedException(
                $"PHD2 guiding is blocked because calibration validation is stale ({validationAge}).",
                validation);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private void InvokeEventHandlersSafely<T>(EventHandler<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler.Invoke(this, value);
            }
            catch
            {
            }
        }
    }

    private static Phd2EquipmentDevice? ParseDevice(JsonElement equipment, string propertyName)
    {
        if (!equipment.TryGetProperty(propertyName, out var device) || device.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new Phd2EquipmentDevice(
            GetRequiredString(device, "name"),
            GetOptionalBoolean(device, "connected") ?? false);
    }

    private static Phd2Point? ParsePointArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new Phd2Exception("PHD2 point result was not a two-element array.");
        }

        var values = element.EnumerateArray().ToArray();
        if (values.Length != 2 || !values[0].TryGetDouble(out var x) || !values[1].TryGetDouble(out var y))
        {
            throw new Phd2Exception("PHD2 point result was not a numeric two-element array.");
        }

        return new Phd2Point(x, y);
    }

    private async Task<Phd2Point?> GetLockPositionCoreAsync(CancellationToken cancellationToken)
    {
        var result = await InvokeAsync("get_lock_position", parameters: null, cancellationToken)
            .ConfigureAwait(false);
        var lockPosition = ParsePointArray(result);
        if (lockPosition is not null &&
            (!double.IsFinite(lockPosition.X) || !double.IsFinite(lockPosition.Y) ||
             lockPosition.X < 0 || lockPosition.Y < 0))
        {
            throw new Phd2Exception(
                "PHD2 get_lock_position returned coordinates that were negative or non-finite.");
        }
        UpdateSnapshot(current => current with { LockPosition = lockPosition });
        return lockPosition;
    }

    private static void ValidateExactLockPositionRequest(Phd2ExactLockPositionRequest request)
    {
        ValidateFiniteNonNegativePoint(request.ExpectedCurrentPosition, nameof(request.ExpectedCurrentPosition));
        ValidateFiniteNonNegativePoint(request.DesiredPosition, nameof(request.DesiredPosition));
        if (!double.IsFinite(request.MaximumExpectedCurrentErrorPixels) ||
            request.MaximumExpectedCurrentErrorPixels < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Maximum expected-current error must be finite and non-negative.");
        }
        if (!double.IsFinite(request.MaximumStepPixels) || request.MaximumStepPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Maximum step must be finite and positive.");
        }
        if (!double.IsFinite(request.MaximumVerificationErrorPixels) ||
            request.MaximumVerificationErrorPixels < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Maximum verification error must be finite and non-negative.");
        }
    }

    private static void ValidateFiniteNonNegativePoint(Phd2Point point, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(point, parameterName);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "PHD2 image coordinates must be finite and non-negative.");
        }
    }

    private static double Distance(Phd2Point first, Phd2Point second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        return Math.Sqrt((x * x) + (y * y));
    }

    private static Phd2Point? ParsePointObject(JsonElement element)
    {
        var x = GetOptionalDouble(element, "X");
        var y = GetOptionalDouble(element, "Y");
        return x.HasValue && y.HasValue ? new Phd2Point(x.Value, y.Value) : null;
    }

    private static Phd2SettleProgress? ParseSettleProgress(JsonElement element)
    {
        var distance = GetOptionalDouble(element, "Distance");
        var time = GetOptionalDouble(element, "Time");
        var stable = GetOptionalDouble(element, "SettleTime");
        var locked = GetOptionalBoolean(element, "StarLocked");
        return distance.HasValue && time.HasValue && stable.HasValue && locked.HasValue
            ? new Phd2SettleProgress(distance.Value, time.Value, stable.Value, locked.Value)
            : null;
    }

    private static Phd2SettleResult ParseSettleResult(Phd2EventMessage message)
    {
        var status = GetOptionalInt32(message.Payload, "Status") ?? 1;
        return new Phd2SettleResult(
            Succeeded: status == 0,
            Error: GetOptionalString(message.Payload, "Error"),
            TotalFrames: GetOptionalInt32(message.Payload, "TotalFrames") ?? 0,
            DroppedFrames: GetOptionalInt32(message.Payload, "DroppedFrames") ?? 0,
            CompletedUtc: message.ReceivedUtc);
    }

    private static Phd2GuideStep ParseGuideStep(JsonElement element)
    {
        return new Phd2GuideStep(
            GetOptionalInt64(element, "Frame"),
            GetOptionalDouble(element, "dx"),
            GetOptionalDouble(element, "dy"),
            GetOptionalDouble(element, "SNR"),
            GetOptionalDouble(element, "HFD"),
            GetOptionalDouble(element, "AvgDist"),
            GetOptionalInt32(element, "ErrorCode"));
    }

    private static string? BuildVersion(JsonElement element)
    {
        var version = GetOptionalString(element, "PHDVersion");
        var subversion = GetOptionalString(element, "PHDSubver");
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(subversion) ? version : $"{version} {subversion}";
    }

    private static Phd2AppState ParseAppState(string? value)
    {
        return value switch
        {
            "Stopped" => Phd2AppState.Stopped,
            "Selected" => Phd2AppState.Selected,
            "Calibrating" => Phd2AppState.Calibrating,
            "Guiding" => Phd2AppState.Guiding,
            "LostLock" => Phd2AppState.LostLock,
            "Paused" => Phd2AppState.Paused,
            "Looping" => Phd2AppState.Looping,
            _ => Phd2AppState.Unknown,
        };
    }

    private static bool IsIdle(Phd2AppState state) =>
        state is Phd2AppState.Stopped or Phd2AppState.Selected;

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        return GetOptionalString(element, propertyName)
            ?? throw new Phd2Exception($"PHD2 response omitted required string property '{propertyName}'.");
    }

    private static int GetRequiredInt32(JsonElement element, string propertyName)
    {
        return GetOptionalInt32(element, propertyName)
            ?? throw new Phd2Exception($"PHD2 response omitted required integer property '{propertyName}'.");
    }

    private static bool GetRequiredBoolean(JsonElement element, string propertyName)
    {
        return GetOptionalBoolean(element, propertyName)
            ?? throw new Phd2Exception($"PHD2 response omitted required boolean property '{propertyName}'.");
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? GetOptionalBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static int? GetOptionalInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static long? GetOptionalInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static double? GetOptionalDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetDouble(out var value)
            ? value
            : null;
    }

    private static void ValidateIdentityRequirement(Phd2IdentityRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (requirement.ProfileId <= 0 || string.IsNullOrWhiteSpace(requirement.ProfileName) ||
            string.IsNullOrWhiteSpace(requirement.CameraName) || string.IsNullOrWhiteSpace(requirement.MountName))
        {
            throw new ArgumentException("Expected profile id, profile name, camera name, and mount name are required.", nameof(requirement));
        }
    }

    private static void ValidateCalibrationRequirement(Phd2CalibrationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (requirement.ProfileId <= 0 || string.IsNullOrWhiteSpace(requirement.ProfileName))
        {
            throw new ArgumentException("Expected calibration profile id and name are required.", nameof(requirement));
        }

        if (requirement.MaximumAge <= TimeSpan.Zero ||
            !double.IsFinite(requirement.MaximumOrthogonalityErrorDegrees) ||
            requirement.MaximumOrthogonalityErrorDegrees is < 0 or > 90 ||
            !double.IsFinite(requirement.MinimumAxisRatePixelsPerSecond) ||
            !double.IsFinite(requirement.MaximumAxisRatePixelsPerSecond) ||
            requirement.MinimumAxisRatePixelsPerSecond <= 0 ||
            requirement.MaximumAxisRatePixelsPerSecond <= requirement.MinimumAxisRatePixelsPerSecond)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requirement),
                "Calibration age, orthogonality limit, and finite positive axis-rate bounds must be valid.");
        }
    }

    private static void ValidateAxisRate(
        string axis,
        double? rate,
        double minimum,
        double maximum,
        ICollection<string> failures)
    {
        if (!IsFinite(rate) || rate <= 0)
        {
            failures.Add($"calibration {axis} rate is missing, non-finite, or non-positive");
        }
        else if (rate < minimum || rate > maximum)
        {
            failures.Add($"calibration {axis} rate {rate.Value:F3} px/s is outside [{minimum:F3}, {maximum:F3}] px/s");
        }
    }

    private static bool IsFinite(double? value)
    {
        return value.HasValue && double.IsFinite(value.Value);
    }

    private static double CalculateOrthogonalityErrorDegrees(double firstAngle, double secondAngle)
    {
        var separation = Math.Abs(firstAngle - secondAngle) % 360;
        if (separation > 180)
        {
            separation = 360 - separation;
        }

        return Math.Abs(90 - separation);
    }

    private static void ValidateDevice(
        string role,
        Phd2EquipmentDevice? actual,
        string expectedName,
        bool requireConnected,
        ICollection<string> failures)
    {
        if (actual is null)
        {
            failures.Add($"{role} is not configured");
            return;
        }

        if (!string.Equals(actual.Name, expectedName, StringComparison.Ordinal))
        {
            failures.Add($"{role} name is '{actual.Name}', expected '{expectedName}'");
        }

        if (requireConnected && !actual.Connected)
        {
            failures.Add($"{role} '{actual.Name}' is not connected");
        }
    }

    private static void ValidateSingleFrameRequest(Phd2SingleFrameRequest request)
    {
        if (request.ExposureMs is < 1 or > 600_000)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Exposure must be between 1 and 600000 ms.");
        }

        if (request.Binning < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Binning must be at least 1.");
        }

        if (request.GainPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "PHD2 gain must be between 0 and 100 percent.");
        }

        if (string.IsNullOrWhiteSpace(request.DestinationPath) || !Path.IsPathFullyQualified(request.DestinationPath))
        {
            throw new ArgumentException("PHD2 capture destination must be an absolute path.", nameof(request));
        }

        var fullPath = Path.GetFullPath(request.DestinationPath);
        if (File.Exists(fullPath))
        {
            throw new IOException($"PHD2 capture destination already exists: '{fullPath}'.");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"PHD2 capture destination directory does not exist: '{directory}'.");
        }
    }

    private static void ValidateGuidingFrameRequest(Phd2GuidingFrameRequest request)
    {
        ValidateImmutableDestination(request.DestinationPath, nameof(request));
        if (request.FreshGuideStepTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Fresh GuideStep timeout must be positive.");
        }
    }

    private static void ValidateImmutableDestination(string destinationPath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("PHD2 capture destination must be an absolute path.", parameterName);
        }

        var fullPath = Path.GetFullPath(destinationPath);
        if (File.Exists(fullPath))
        {
            throw new IOException($"PHD2 capture destination already exists: '{fullPath}'.");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"PHD2 capture destination directory does not exist: '{directory}'.");
        }
    }

    private static void ValidateSettleCriteria(Phd2SettleCriteria criteria)
    {
        if (!double.IsFinite(criteria.Pixels) || criteria.Pixels <= 0 ||
            criteria.StableTimeSeconds <= 0 || criteria.TimeoutSeconds <= 0 ||
            criteria.TimeoutSeconds < criteria.StableTimeSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(criteria),
                "Settle tolerance must be positive and timeout must be at least the positive stable-time interval.");
        }
    }

    private static bool PathEquals(string first, string second)
    {
        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record JsonRpcRequest(
        [property: JsonPropertyName("jsonrpc")] string JsonRpc,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] object? Parameters,
        [property: JsonPropertyName("id")] long Id);

    private sealed class EventWaiter(Func<Phd2EventMessage, bool> predicate)
    {
        public Func<Phd2EventMessage, bool> Predicate { get; } = predicate;

        public TaskCompletionSource<Phd2EventMessage> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class EventWaiterRegistration(
        Phd2Client owner,
        Guid id,
        Task<Phd2EventMessage> task) : IDisposable
    {
        private bool disposed;

        public Task<Phd2EventMessage> Task { get; } = task;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            owner.RemoveEventWaiter(id);
        }
    }
}
