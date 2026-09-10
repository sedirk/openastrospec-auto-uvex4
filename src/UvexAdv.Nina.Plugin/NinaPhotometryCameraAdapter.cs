using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Nina.Plugin;

/// <summary>Only these three device mediators are available to the worker.
/// All exposure, filter-offset focus, display and raw saving uses N.I.N.A.;
/// this adapter never loads a camera SDK or opens a device directly.</summary>
internal sealed class NinaPhotometryCameraAdapter(
    IProfileService profiles, ICameraMediator camera, IFilterWheelMediator wheel,
    IFocuserMediator focuser, IImagingMediator imaging, IImageSaveMediator saves,
    UvexPluginSettings settings, Action validateConfiguration,
    IProgress<ApplicationStatus> progress, Func<object?>? sequenceCaptureOwner = null) : IQhyCheckpointCameraAdapter, IQhyNativeFramePersistence
{
    private readonly ConcurrentDictionary<QhyFrame, IImageData> pending = new();
    private readonly Dictionary<string, QhyFocusBudget> focusBudgets = new(StringComparer.Ordinal);
    private string? focusRunId;
    private QhyFocusBudget? focusBudget;
    internal void BindFocusPolicy(string runId, QhyFocusRunPolicy policy, Func<QhyFocusBudget> createBudget)
    {
        if (policy.DeviceId != settings.PhotometryFocuserId || policy.MinimumPositionSteps >= policy.MaximumPositionSteps ||
            policy.MaximumSingleMoveSteps < 0 || policy.MaximumCumulativeMoveSteps < policy.MaximumSingleMoveSteps)
            throw new InvalidOperationException("PHOTOMETRY_FOCUS_POLICY_INVALID: Bind the measured photometry focuser and valid bounds.");
        if (focusBudget?.Pending == true && focusRunId != runId)
            throw new InvalidOperationException("PHOTOMETRY_FOCUS_PENDING: A new job cannot erase an unresolved focus action.");
        if (!focusBudgets.TryGetValue(runId, out var budget)) focusBudgets[runId] = budget = createBudget();
        if (budget.Policy != policy) throw new InvalidOperationException("PHOTOMETRY_FOCUS_POLICY_CHANGED: The run's focus budget cannot be replaced.");
        focusRunId = runId; focusBudget = budget;
    }
    public string AdapterName => NinaInstancePolicy.WorkerAdapter;
    public QhyCameraStatus Status
    {
        get
        {
            var c = camera.GetInfo();
            var w = wheel.GetInfo();
            var f = focuser.GetInfo();
            return new(c.Connected, c.Connected ? new(c.DeviceId, c.Name, AdapterName) : null,
                c.Connected ? c.Temperature : null, c.Connected ? c.CoolerPower : null,
                null, DateTimeOffset.UtcNow,
                new(!string.IsNullOrWhiteSpace(settings.PhotometryFilterWheelId),
                    w.Connected && !w.IsMoving && w.SelectedFilter is not null,
                    w.SelectedFilter?.Position, w.SelectedFilter?.Name, null, DateTimeOffset.UtcNow),
                new(f.Connected, f.DeviceId, f.Connected ? f.Position : null, focusRunId,
                    focusBudget is { Pending: false } && f.Connected && !f.IsMoving &&
                        f.Position == focusBudget.ExpectedPosition, DateTimeOffset.UtcNow));
        }
    }

    public async Task<QhyCameraIdentity> ConnectExactAsync(string expectedStableId, string expectedModel, CancellationToken token)
    {
        validateConfiguration();
        token.ThrowIfCancellationRequested();
        if (expectedStableId != settings.PhotometryCameraId || !expectedStableId.Contains(expectedModel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PHOTOMETRY_CAMERA_BINDING_MISMATCH: Exact QHY identity is required.");
        if (!camera.GetInfo().Connected && !await camera.Connect()) throw new IOException("N.I.N.A. QHY connect failed.");
        validateConfiguration();
        RequireConnectedCamera();
        token.ThrowIfCancellationRequested();
        if (!focuser.GetInfo().Connected && !await focuser.Connect()) throw new IOException("Photometry focuser connect failed.");
        validateConfiguration();
        token.ThrowIfCancellationRequested();
        if (!wheel.GetInfo().Connected && !await wheel.Connect()) throw new IOException("Photometry filter-wheel connect failed.");
        RequireOwnedAccessories();
        return Status.Identity!;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        // Job cancellation does not silently change native Profile connections.
        // Explicit operator disconnection remains in the owning N.I.N.A.
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<QhyFilterWheelStatus> ReadFilterWheelStatusAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(Status.FilterWheel!); }

    public async Task<QhyFilterWheelStatus> SelectFilterAsync(string name, CancellationToken token)
    {
        validateConfiguration();
        RequireOwnedAccessories();
        var matches = profiles.ActiveProfile.FilterWheelSettings.FilterWheelFilters
            .Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("PHOTOMETRY_FILTER_AMBIGUOUS: Expected exactly one configured physical filter slot.");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(90));
        if (focusBudget is null) throw new InvalidOperationException("PHOTOMETRY_FOCUS_POLICY_REQUIRED: Night Setup focus bounds must be supplied before capture.");
        if (focuser.GetInfo().IsMoving || focuser.GetInfo().Position != focusBudget.ExpectedPosition || focusBudget.Pending)
            throw new InvalidOperationException("PHOTOMETRY_FOCUS_CHANGED: Manual or unconfirmed focus movement requires operator review.");
        // Native ChangeFilter owns the configured photometry focus offsets too.
        if (wheel.GetInfo().SelectedFilter?.Position != matches[0].Position)
        {
            var previous = wheel.GetInfo().SelectedFilter ?? throw new InvalidOperationException("PHOTOMETRY_FILTER_ORIGIN_UNKNOWN: The initial physical slot must be known before filter offsets.");
            var previousConfigured = profiles.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Single(f => f.Position == previous.Position);
            var offset = profiles.ActiveProfile.FocuserSettings.UseFilterWheelOffsets ? matches[0].FocusOffset - previousConfigured.FocusOffset : 0;
            var focusSettings = profiles.ActiveProfile.FocuserSettings;
            var overshoot = 0;
            if (offset != 0)
            {
                if (focusSettings.BacklashCompensationModel == NINA.Core.Enum.BacklashCompensationModel.ABSOLUTE)
                    throw new InvalidOperationException("PHOTOMETRY_FOCUS_BACKLASH_UNOBSERVABLE: Absolute compensation hides a physical offset; commission None or Overshoot for this bounded worker.");
                if (focusSettings.BacklashCompensationModel == NINA.Core.Enum.BacklashCompensationModel.OVERSHOOT)
                    overshoot = offset < 0 ? -focusSettings.BacklashIn : focusSettings.BacklashOut;
            }
            focusBudget.Reserve(focuser.GetInfo().Position, offset, overshoot);
            await wheel.ChangeFilter(matches[0], bounded.Token, progress);
            if (focuser.GetInfo().IsMoving) throw new IOException("PHOTOMETRY_FOCUS_STILL_MOVING: Native filter-focus action has not stopped.");
            focusBudget.Confirm(focuser.GetInfo().Position);
        }
        validateConfiguration();
        RequireOwnedAccessories();
        var result = Status.FilterWheel!;
        if (!result.PositionKnown || result.Position != matches[0].Position)
            throw new IOException("PHOTOMETRY_FILTER_POSITION_UNCONFIRMED: Native wheel did not confirm the selected slot.");
        return result;
    }

    public Task<QhyFrame> CaptureSingleFrameAsync(QhyFrameSettings request, CancellationToken token) =>
        throw new InvalidOperationException("Native worker capture requires the active coordinator checkpoint.");

    public async Task<QhyFrame> CaptureWithCheckpointAsync(QhyFrameSettings request,
        Func<CancellationToken, Task> checkpoint, CancellationToken token)
    {
        var nativeSequence = sequenceCaptureOwner?.Invoke();
        var owner = nativeSequence ?? this;
        if (!camera.IsFreeToCapture(owner)) throw new InvalidOperationException("PHOTOMETRY_CAMERA_BUSY: Another native task owns capture.");
        if (nativeSequence is null) camera.RegisterCaptureBlock(this);
        try { return await CaptureOwnedFrameAsync(request, checkpoint, token); }
        finally { if (nativeSequence is null) camera.ReleaseCaptureBlock(this); }
    }

    private async Task<QhyFrame> CaptureOwnedFrameAsync(QhyFrameSettings request,
        Func<CancellationToken, Task> checkpoint, CancellationToken token)
    {
        validateConfiguration();
        RequireConnectedCamera();
        RequireOwnedAccessories();
        if (request.BitDepth != 16 || request.RoiX != 0 || request.RoiY != 0 || request.RoiWidth != 0 || request.RoiHeight != 0)
            throw new InvalidOperationException("PHOTOMETRY_FRAME_MODE_UNSUPPORTED: Initial native worker requires full-frame 16-bit capture.");
        await checkpoint(token);
        await SelectFilterAsync(request.FilterName, token);
        await checkpoint(token);
        validateConfiguration();
        token.ThrowIfCancellationRequested();
        camera.SetReadoutModeForNormalImages(checked((short)request.ReadoutMode));
        camera.SetUSBLimit(request.UsbTraffic);
        if (request.TargetTemperatureC is { } target &&
            (!camera.GetInfo().CoolerOn || Math.Abs(camera.GetInfo().TemperatureSetPoint - target) > 0.05))
        {
            using var cooling = CancellationTokenSource.CreateLinkedTokenSource(token);
            cooling.CancelAfter(TimeSpan.FromMinutes(5));
            if (!await camera.CoolCamera(target, TimeSpan.Zero, progress, cooling.Token))
                throw new IOException("PHOTOMETRY_COOLING_UNCONFIRMED: Native cooler did not accept the requested target.");
        }
        await checkpoint(token);
        validateConfiguration();
        var sequence = new CaptureSequence
        {
            ExposureTime = request.ExposureSeconds, ImageType = CaptureSequence.ImageTypes.LIGHT,
            Binning = new BinningMode(checked((short)request.BinningX), checked((short)request.BinningY)),
            Gain = request.Gain, Offset = request.Offset, TotalExposureCount = 1, Dither = false,
        };
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(request.ExposureSeconds + 120));
        var exposure = await imaging.CaptureImage(sequence, bounded.Token, progress, "OpenAstroSpec Photometry");
        var image = await exposure.ToImageData(progress, bounded.Token);
        if (image.Properties.IsBayered || image.Data.FlatArray.Length != image.Properties.Width * image.Properties.Height)
            throw new InvalidDataException("PHOTOMETRY_NATIVE_FRAME_INVALID: Expected a monochrome full image.");
        if (image.MetaData.Camera.Id != settings.PhotometryCameraId ||
            image.MetaData.Camera.Gain != request.Gain || image.MetaData.Camera.Offset != request.Offset ||
            image.MetaData.Camera.BinX != request.BinningX || image.MetaData.Camera.BinY != request.BinningY ||
            image.MetaData.Camera.ReadoutModeIndex != request.ReadoutMode ||
            Math.Abs(image.MetaData.Image.ExposureTime - request.ExposureSeconds) > Math.Max(0.001, request.ExposureSeconds * 0.01))
            throw new InvalidDataException("PHOTOMETRY_NATIVE_READBACK_MISMATCH: Camera, exposure, gain, offset, bin or readout metadata did not match the request.");
        var start = image.MetaData.Image.ExposureStart;
        if (start == DateTime.MinValue) throw new InvalidDataException("PHOTOMETRY_NATIVE_TIME_MISSING: Native exposure start is unknown.");
        var started = new DateTimeOffset(start.ToUniversalTime());
        var frame = new QhyFrame(image.Properties.Width, image.Properties.Height, image.Data.FlatArray,
            started, started.AddSeconds(image.MetaData.Image.ExposureTime), request,
            new(settings.PhotometryCameraId, "QHYminiCam8M", AdapterName),
            TimingSource: "nina-software-start-plus-exposure-duration; uncertainty unmeasured");
        pending[frame] = image;
        return frame;
    }

    public async Task<string> SaveNativeFrameAsync(QhyJobSnapshot job, QhyFrame frame,
        Guid frameId, int sequenceNumber, string role, CancellationToken token)
    {
        if (!pending.TryRemove(frame, out var image)) throw new InvalidOperationException("PHOTOMETRY_FRAME_REUSED: No matching unsaved native image.");
        image.MetaData.Target.Name = job.RequestedTarget;
        if (string.IsNullOrWhiteSpace(job.NightSetupId)) throw new InvalidDataException("PHOTOMETRY_NIGHT_SETUP_MISSING: A real shared Night Setup ID is required; it cannot be fabricated from the job ID.");
        var provenance = new FitsProvenanceExpectation(job.RequestedTarget, job.ObservationRunId, role,
            frameId.ToString("N"), job.NightSetupId, CaptureSequence.ImageTypes.LIGHT, "", HeaderSchemaVersion: 2);
        image.MetaData.Target.Name = AtrFitsProvenance.FitsTargetName(provenance);
        foreach (var header in AtrFitsProvenance.CreateIdentityHeaders(provenance))
            image.MetaData.GenericHeaders.Add(new StringMetaDataHeader(header.Key, header.Value, "OpenAstroSpec identity / UTF8-B64"));
        if (job.TargetRightAscensionDegrees is { } ra && job.TargetDeclinationDegrees is { } dec)
            image.MetaData.Target.Coordinates = new Coordinates(ra, dec, Epoch.J2000, Coordinates.RAType.Degrees);
        image.MetaData.Sequence.Title = "OpenAstroSpec Photometry · " + job.RequestedTarget;
        image.MetaData.Image.ExposureNumber = sequenceNumber;
        foreach (var entry in new Dictionary<string, string>
        {
            ["QHYCID"] = frameId.ToString("N"), ["QHYJOB"] = job.Id.ToString("N"),
            ["NINAPRF"] = profiles.ActiveProfile.Id.ToString(),
            ["TIMESRC"] = "NINA software start + exposure duration; not hardware synchronized",
        }) image.MetaData.GenericHeaders.Add(new StringMetaDataHeader(entry.Key, entry.Value, "OpenAstroSpec photometry provenance"));
        var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSaved(object? sender, ImageSavedEventArgs e)
        {
            var id = e.MetaData.GenericHeaders.OfType<IGenericMetaDataHeader<string>>().FirstOrDefault(h => h.Key == "QHYCID")?.Value;
            if (id != frameId.ToString("N")) return;
            if (!e.PathToImage.IsFile) saved.TrySetException(new InvalidDataException("Native saving did not report a local path."));
            else saved.TrySetResult(e.PathToImage.LocalPath);
        }
        saves.ImageSaved += OnSaved;
        try
        {
            // Retain an already acquired frame even when a stop arrives during saving.
            using var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var rendered = imaging.PrepareImage(image, new PrepareImageParameters(true, false), saveTimeout.Token);
            await saves.Enqueue(image, rendered, progress, saveTimeout.Token);
            var path = await saved.Task.WaitAsync(saveTimeout.Token);
            var readback = QhyFitsCodec.Read(path);
            var verified = AtrFitsProvenance.Verify(path, provenance);
            if (!readback.Header.TryGetValue("QHYCID", out var id) || id != frameId.ToString("N") ||
                readback.Width != frame.Width || readback.Height != frame.Height || !verified.IsValid)
                throw new InvalidDataException("PHOTOMETRY_FITS_PROVENANCE_MISMATCH: Immutable native FITS does not match the captured image.");
            return path;
        }
        finally { saves.ImageSaved -= OnSaved; }
    }

    private void RequireConnectedCamera()
    {
        var info = camera.GetInfo();
        if (!info.Connected || info.DeviceId != settings.PhotometryCameraId)
            throw new InvalidOperationException("PHOTOMETRY_CAMERA_CHANGED: The bound QHY must remain connected.");
    }

    private void RequireOwnedAccessories()
    {
        if (!wheel.GetInfo().Connected || wheel.GetInfo().DeviceId != settings.PhotometryFilterWheelId ||
            !focuser.GetInfo().Connected || focuser.GetInfo().DeviceId != settings.PhotometryFocuserId)
            throw new InvalidOperationException("PHOTOMETRY_ACCESSORY_CHANGED: Only the bound photometry wheel/focuser are permitted.");
    }

    public ValueTask DisposeAsync() { pending.Clear(); return ValueTask.CompletedTask; }
}
