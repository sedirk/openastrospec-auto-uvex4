using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Qhy.Service.Adapters;

public sealed class NativeQhyCameraAdapter : IQhyCameraAdapter
{
    private const uint Success = 0;
    private const uint ReadDirectly = 0x2001;
    private const uint Delay200Milliseconds = 0x2000;
    private const int ControlGain = 6;
    private const int ControlOffset = 7;
    private const int ControlExposure = 8;
    private const int ControlTransferBits = 10;
    private const int ControlUsbTraffic = 12;
    private const int ControlCurrentTemperature = 14;
    private const int ControlCurrentPwm = 15;
    private static readonly object ResolverGate = new();
    private static string? resolvedSdkPath;
    private readonly QhyServiceOptions options;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly object statusGate = new();
    private readonly object disposalGate = new();
    private QhyCameraStatus status = new(false, null, null, null, null, DateTimeOffset.UtcNow);
    private QhyCameraIdentity? identity;
    private IntPtr handle;
    private bool resourcesInitialized;
    private uint chipWidth;
    private uint chipHeight;
    private int activeReadoutMode;
    private CancellationTokenSource? temperatureCancellation;
    private Task? temperatureWorker;
    private double? targetTemperatureC;
    private Task? disposalTask;
    private int disposalState;

    public NativeQhyCameraAdapter(QhyServiceOptions options)
    {
        this.options = options;
        QhyServiceConfigurationProof.ValidateFilterPositions(options.NativeFilterPositions, requireConfigured: false);
    }

    public string AdapterName => "qhy-native";

    public QhyCameraStatus Status
    {
        get
        {
            lock (statusGate) return status;
        }
    }

    public async Task<QhyCameraIdentity> ConnectExactAsync(
        string expectedStableId,
        string expectedModel,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();
        if (string.IsNullOrWhiteSpace(expectedStableId))
        {
            throw new QhyAdapterException("An exact QHY stable ID is required; ordinal selection is forbidden.");
        }

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            if (Status.Connected && identity is not null)
            {
                ValidateExactIdentity(identity, expectedStableId, expectedModel);
                return identity;
            }

            ConfigureNativeLibrary(options.NativeSdkPath, options.NativeSdkSha256);
            Check(NativeMethods.InitQHYCCDResource(), "initialize QHY SDK resources");
            resourcesInitialized = true;
            var count = NativeMethods.ScanQHYCCD();
            if (count > 64) throw new QhyAdapterException($"QHY SDK scan failed with code 0x{count:X8}.");

            string? exactId = null;
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var idBuffer = new byte[256];
                Check(NativeMethods.GetQHYCCDId(index, idBuffer), $"read QHY stable ID at enumeration index {index}");
                var candidate = NullTerminatedAscii(idBuffer);
                if (string.Equals(candidate, expectedStableId, StringComparison.Ordinal)) exactId = candidate;
            }

            if (exactId is null)
            {
                throw new QhyAdapterException(
                    $"The configured QHY camera '{expectedStableId}' was not found among {count} QHY device(s); no ordinal fallback was attempted.");
            }

            var modelBuffer = new byte[256];
            Check(NativeMethods.GetQHYCCDModel(exactId, modelBuffer), "read QHY model");
            var model = NullTerminatedAscii(modelBuffer);
            if (!string.Equals(model, expectedModel, StringComparison.OrdinalIgnoreCase))
            {
                throw new QhyAdapterException($"QHY model mismatch. Expected '{expectedModel}', received '{model}'.");
            }

            handle = NativeMethods.OpenQHYCCD(exactId);
            if (handle == IntPtr.Zero) throw new QhyAdapterException($"QHY SDK could not open exact device '{expectedStableId}'.");
            Check(NativeMethods.SetQHYCCDStreamMode(handle, 0), "select single-frame stream mode");
            Check(NativeMethods.SetQHYCCDReadMode(handle, checked((uint)options.NativeReadoutMode)), "select configured readout mode");
            activeReadoutMode = options.NativeReadoutMode;
            Check(NativeMethods.InitQHYCCD(handle), "initialize exact QHY camera");
            Check(
                NativeMethods.GetQHYCCDChipInfo(handle, out _, out _, out chipWidth, out chipHeight, out _, out _, out _),
                "read QHY chip dimensions");

            NativeMethods.GetQHYCCDSDKVersion(out var year, out var month, out var day, out var subday);
            identity = new QhyCameraIdentity(exactId, model, AdapterName, $"{year}-{month}-{day}-{subday}");
            var filterWheel = ReadFilterWheelStatusCore(handle);
            lock (statusGate)
            {
                status = new QhyCameraStatus(
                    true,
                    identity,
                    ReadOptionalParam(ControlCurrentTemperature),
                    ReadOptionalParam(ControlCurrentPwm),
                    null,
                    DateTimeOffset.UtcNow,
                    filterWheel);
            }

            temperatureCancellation = new CancellationTokenSource();
            temperatureWorker = TemperatureLoopAsync(temperatureCancellation.Token);
            return identity;
        }
        catch
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        Task? activeDisposal;
        lock (disposalGate) activeDisposal = disposalTask;
        if (activeDisposal is not null)
        {
            await activeDisposal.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await DisconnectSerializedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DisconnectSerializedAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DisconnectCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                captureGate.Release();
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<QhyFilterWheelStatus> ReadFilterWheelStatusAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();
        await captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            var currentHandle = handle;
            if (!Status.Connected || currentHandle == IntPtr.Zero)
            {
                throw new QhyAdapterException("QHY camera is not connected.");
            }

            var filterWheel = ReadFilterWheelStatusCore(currentHandle);
            UpdateFilterWheelStatus(filterWheel);
            return filterWheel;
        }
        finally
        {
            captureGate.Release();
        }
    }

    public async Task<QhyFilterWheelStatus> SelectFilterAsync(string filterName, CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();
        await captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            var currentHandle = handle;
            if (!Status.Connected || currentHandle == IntPtr.Zero)
            {
                throw new QhyAdapterException("QHY camera is not connected.");
            }

            return await SelectFilterCoreAsync(currentHandle, filterName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            captureGate.Release();
        }
    }

    public async Task<QhyFrame> CaptureSingleFrameAsync(QhyFrameSettings settings, CancellationToken cancellationToken)
    {
        ThrowIfDisposingOrDisposed();
        await captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposingOrDisposed();
            var currentHandle = handle;
            var currentIdentity = identity;
            if (!Status.Connected || currentHandle == IntPtr.Zero || currentIdentity is null)
            {
                throw new QhyAdapterException("QHY camera is not connected.");
            }

            if (settings.ReadoutMode != activeReadoutMode)
            {
                throw new QhyAdapterException(
                    $"Job requested readout mode {settings.ReadoutMode}, but the identity-bound native adapter was initialized in mode {activeReadoutMode}. Reconnect with a versioned preset instead of changing modes mid-run.");
            }

            ValidateSettings(settings);
            targetTemperatureC = settings.TargetTemperatureC;
            SetRequiredParam(currentHandle, ControlGain, settings.Gain, "gain");
            SetRequiredParam(currentHandle, ControlOffset, settings.Offset, "offset");
            SetRequiredParam(currentHandle, ControlExposure, settings.ExposureSeconds * 1_000_000, "exposure");
            SetOptionalParam(currentHandle, ControlUsbTraffic, settings.UsbTraffic, "USB traffic");
            SetOptionalParam(currentHandle, ControlTransferBits, settings.BitDepth, "transfer bit depth");
            Check(NativeMethods.SetQHYCCDBitsMode(currentHandle, checked((uint)settings.BitDepth)), "set output bit depth");
            Check(
                NativeMethods.SetQHYCCDBinMode(currentHandle, checked((uint)settings.BinningX), checked((uint)settings.BinningY)),
                "set binning");

            var fullWidth = checked((int)chipWidth / settings.BinningX);
            var fullHeight = checked((int)chipHeight / settings.BinningY);
            var roiWidth = settings.RoiWidth > 0 ? settings.RoiWidth : fullWidth;
            var roiHeight = settings.RoiHeight > 0 ? settings.RoiHeight : fullHeight;
            ValidateRoi(settings, fullWidth, fullHeight, roiWidth, roiHeight);
            Check(
                NativeMethods.SetQHYCCDResolution(
                    currentHandle,
                    checked((uint)settings.RoiX),
                    checked((uint)settings.RoiY),
                    checked((uint)roiWidth),
                    checked((uint)roiHeight)),
                "set ROI");
            var selectedFilter = await SelectFilterCoreAsync(currentHandle, settings.FilterName, cancellationToken).ConfigureAwait(false);
            settings = settings with
            {
                FilterName = selectedFilter.FilterName ?? throw new QhyAdapterException(
                    "Integrated QHY filter selection completed without an attested configured name."),
            };

            var memoryLength = NativeMethods.GetQHYCCDMemLength(currentHandle);
            var minimumLength = checked((uint)(roiWidth * roiHeight * Math.Max(1, settings.BitDepth / 8)));
            if (memoryLength < minimumLength || memoryLength > int.MaxValue)
            {
                throw new QhyAdapterException($"QHY SDK reported invalid frame buffer length {memoryLength} bytes.");
            }

            var buffer = new byte[memoryLength];
            var started = DateTimeOffset.UtcNow;
            var timeoutMilliseconds = checked((uint)Math.Min(
                uint.MaxValue,
                Math.Ceiling(settings.ExposureSeconds * 1_000) + 30_000));
            Check(NativeMethods.SetQHYCCDSingleFrameTimeOut(currentHandle, timeoutMilliseconds), "set bounded single-frame timeout");
            var exposureResult = NativeMethods.ExpQHYCCDSingleFrame(currentHandle);
            if (exposureResult is not (Success or ReadDirectly or Delay200Milliseconds))
            {
                throw new QhyAdapterException($"QHY exposure start failed with code 0x{exposureResult:X8}.");
            }

            using var cancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var cameraHandle = (IntPtr)state!;
                    if (cameraHandle != IntPtr.Zero) _ = NativeMethods.CancelQHYCCDExposingAndReadout(cameraHandle);
                },
                currentHandle);
            var readResult = await Task.Run(
                    () =>
                    {
                        var result = NativeMethods.GetQHYCCDSingleFrame(
                            currentHandle,
                            out var width,
                            out var height,
                            out var bitsPerPixel,
                            out var channels,
                            buffer);
                        return new NativeFrameResult(result, width, height, bitsPerPixel, channels);
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Check(readResult.Result, "read single QHY frame");
            if (readResult.Channels != 1 || readResult.Width == 0 || readResult.Height == 0)
            {
                throw new QhyAdapterException(
                    $"QHY returned unsupported frame geometry {readResult.Width}x{readResult.Height}, channels={readResult.Channels}.");
            }

            var pixelCount = checked((int)(readResult.Width * readResult.Height));
            var pixels = ConvertPixels(buffer, pixelCount, readResult.BitsPerPixel);
            var ended = DateTimeOffset.UtcNow;
            UpdateStatus(null);
            return new QhyFrame(checked((int)readResult.Width), checked((int)readResult.Height), pixels, started, ended, settings, currentIdentity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            UpdateStatus(ex.Message);
            throw ex is QhyAdapterException ? ex : new QhyAdapterException("Native QHY single-frame capture failed.", ex);
        }
        finally
        {
            captureGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            disposalTask ??= DisposeOnceAsync();
            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeOnceAsync()
    {
        Interlocked.Exchange(ref disposalState, 1);
        try
        {
            await DisconnectSerializedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref disposalState, 2);
        }

        // Do not dispose the semaphore instances. A caller that entered just
        // before disposal was announced may not yet have reached WaitAsync;
        // disposing the gates would turn a safe shutdown race into an
        // ObjectDisposedException or release-on-disposed failure. They contain
        // no native resource unless their WaitHandle property is requested.
    }

    private void ThrowIfDisposingOrDisposed()
    {
        if (Volatile.Read(ref disposalState) != 0)
        {
            throw new ObjectDisposedException(nameof(NativeQhyCameraAdapter));
        }
    }

    private async Task DisconnectCoreAsync()
    {
        var temperatureCts = Interlocked.Exchange(ref temperatureCancellation, null);
        if (temperatureCts is not null)
        {
            temperatureCts.Cancel();
            if (temperatureWorker is not null)
            {
                try
                {
                    await temperatureWorker.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            temperatureCts.Dispose();
            temperatureWorker = null;
        }

        if (handle != IntPtr.Zero)
        {
            _ = NativeMethods.CancelQHYCCDExposingAndReadout(handle);
            _ = NativeMethods.CloseQHYCCD(handle);
            handle = IntPtr.Zero;
        }

        if (resourcesInitialized)
        {
            _ = NativeMethods.ReleaseQHYCCDResource();
            resourcesInitialized = false;
        }

        targetTemperatureC = null;
        lock (statusGate)
        {
            status = new QhyCameraStatus(
                false,
                identity,
                null,
                null,
                status.LastError,
                DateTimeOffset.UtcNow,
                new QhyFilterWheelStatus(
                    options.NativeFilterPositions.Count > 0,
                    false,
                    null,
                    null,
                    "QHY camera is disconnected; the physical filter position is not attested.",
                    DateTimeOffset.UtcNow));
        }
    }

    private async Task TemperatureLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // The vendor SDK exposes a single camera handle and does not promise that
                // temperature/control calls are safe while an exposure is being read out.
                // Serialize every handle call with capture and service disconnect.
                await captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var currentHandle = handle;
                    if (currentHandle != IntPtr.Zero && targetTemperatureC is { } target)
                    {
                        Check(NativeMethods.ControlQHYCCDTemp(currentHandle, target), "maintain QHY target temperature");
                    }

                    if (currentHandle != IntPtr.Zero) UpdateStatus(null, preserveExistingError: true);
                }
                finally
                {
                    captureGate.Release();
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lock (statusGate)
                {
                    status = status with
                    {
                        LastError = $"Temperature control: {ex.Message}",
                        TimestampUtc = DateTimeOffset.UtcNow,
                    };
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<QhyFilterWheelStatus> SelectFilterCoreAsync(
        IntPtr currentHandle,
        string filterName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filterName) ||
            !options.NativeFilterPositions.TryGetValue(filterName.Trim(), out var position))
        {
            throw new QhyAdapterException(
                $"Requested QHY filter '{filterName}' is not present in the explicit filter-position map; no metadata-only fallback is allowed.");
        }

        var canonicalName = CanonicalFilterName(position);
        var initial = ReadFilterWheelStatusCore(currentHandle);
        UpdateFilterWheelStatus(initial);
        if (initial.PositionKnown && initial.Position == position &&
            string.Equals(initial.FilterName, canonicalName, StringComparison.OrdinalIgnoreCase))
        {
            return initial;
        }

        var order = Encoding.ASCII.GetBytes(position.ToString("X1", CultureInfo.InvariantCulture));
        Check(NativeMethods.SendOrder2QHYCCDCFW(currentHandle, order, checked((uint)order.Length)), $"move integrated QHY filter wheel to {filterName}");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        QhyFilterWheelStatus? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = ReadFilterWheelStatusCore(currentHandle);
            UpdateFilterWheelStatus(last);
            if (last.PositionKnown && last.Position == position &&
                string.Equals(last.FilterName, canonicalName, StringComparison.OrdinalIgnoreCase))
            {
                return last;
            }
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        throw new QhyAdapterException(
            $"Integrated QHY filter wheel did not reach configured filter '{canonicalName}' at position {position} within 15 seconds. " +
            $"Last read: position={last?.Position?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, name={last?.FilterName ?? "unknown"}, error={last?.Error ?? "none"}.");
    }

    private QhyFilterWheelStatus ReadFilterWheelStatusCore(IntPtr currentHandle)
    {
        var now = DateTimeOffset.UtcNow;
        if (options.NativeFilterPositions.Count == 0)
        {
            return new QhyFilterWheelStatus(
                false,
                false,
                null,
                null,
                "No explicit integrated filter-wheel position map is configured.",
                now);
        }

        var statusBuffer = new byte[16];
        var result = NativeMethods.GetQHYCCDCFWStatus(currentHandle, statusBuffer);
        if (result != Success)
        {
            return new QhyFilterWheelStatus(
                true,
                false,
                null,
                null,
                $"Failed to read integrated QHY filter-wheel status; QHY SDK code 0x{result:X8}.",
                now);
        }

        var raw = NullTerminatedAscii(statusBuffer);
        if (raw.Length == 0 || !Uri.IsHexDigit(raw[0]))
        {
            return new QhyFilterWheelStatus(
                true,
                false,
                null,
                null,
                $"Integrated QHY filter-wheel status '{raw}' did not contain a hexadecimal position.",
                now);
        }

        var position = int.Parse(raw[..1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var name = options.NativeFilterPositions
            .Where(pair => pair.Value == position)
            .Select(static pair => pair.Key)
            .SingleOrDefault();
        return name is null
            ? new QhyFilterWheelStatus(
                true,
                true,
                position,
                null,
                $"Integrated QHY filter wheel reported position {position}, which is absent from the explicit map.",
                now)
            : new QhyFilterWheelStatus(true, true, position, name, null, now);
    }

    private string CanonicalFilterName(int position) =>
        options.NativeFilterPositions.Single(pair => pair.Value == position).Key;

    private void UpdateFilterWheelStatus(QhyFilterWheelStatus filterWheel)
    {
        lock (statusGate)
        {
            status = status with
            {
                FilterWheel = filterWheel,
                TimestampUtc = filterWheel.TimestampUtc,
            };
        }
    }

    private void UpdateStatus(string? error, bool preserveExistingError = false)
    {
        lock (statusGate)
        {
            status = status with
            {
                TemperatureC = handle == IntPtr.Zero ? null : ReadOptionalParam(ControlCurrentTemperature),
                CoolerPowerPercent = handle == IntPtr.Zero ? null : ReadOptionalParam(ControlCurrentPwm),
                LastError = preserveExistingError && error is null ? status.LastError : error,
                TimestampUtc = DateTimeOffset.UtcNow,
            };
        }
    }

    private double? ReadOptionalParam(int control)
    {
        if (handle == IntPtr.Zero || NativeMethods.IsQHYCCDControlAvailable(handle, control) != Success) return null;
        var value = NativeMethods.GetQHYCCDParam(handle, control);
        return double.IsFinite(value) ? value : null;
    }

    private static void SetRequiredParam(IntPtr cameraHandle, int control, double value, string name)
    {
        if (NativeMethods.IsQHYCCDControlAvailable(cameraHandle, control) != Success)
        {
            throw new QhyAdapterException($"QHY camera does not report required control '{name}'.");
        }

        ValidateControlRange(cameraHandle, control, value, name);
        Check(NativeMethods.SetQHYCCDParam(cameraHandle, control, value), $"set QHY {name}");
    }

    private static void SetOptionalParam(IntPtr cameraHandle, int control, double value, string name)
    {
        if (NativeMethods.IsQHYCCDControlAvailable(cameraHandle, control) != Success) return;
        ValidateControlRange(cameraHandle, control, value, name);
        Check(NativeMethods.SetQHYCCDParam(cameraHandle, control, value), $"set QHY {name}");
    }

    private static void ValidateControlRange(IntPtr cameraHandle, int control, double value, string name)
    {
        Check(NativeMethods.GetQHYCCDParamMinMaxStep(cameraHandle, control, out var minimum, out var maximum, out _), $"query QHY {name} range");
        if (value < minimum || value > maximum)
        {
            throw new QhyAdapterException($"Requested QHY {name} {value:G15} is outside [{minimum:G15}, {maximum:G15}].");
        }
    }

    private static void ValidateSettings(QhyFrameSettings settings)
    {
        if (!double.IsFinite(settings.ExposureSeconds) || settings.ExposureSeconds <= 0 || settings.ExposureSeconds > 86_400)
        {
            throw new QhyAdapterException("QHY exposure must be within (0, 86400] seconds.");
        }

        if (settings.BinningX is < 1 or > 8 || settings.BinningY is < 1 or > 8)
        {
            throw new QhyAdapterException("QHY binning must be within 1-8.");
        }

        if (settings.BitDepth is not (8 or 16)) throw new QhyAdapterException("Native QHY adapter supports 8-bit or 16-bit monochrome frames.");
    }

    private static void ValidateRoi(QhyFrameSettings settings, int fullWidth, int fullHeight, int roiWidth, int roiHeight)
    {
        if (settings.RoiX < 0 || settings.RoiY < 0 || roiWidth <= 0 || roiHeight <= 0 ||
            settings.RoiX + roiWidth > fullWidth || settings.RoiY + roiHeight > fullHeight)
        {
            throw new QhyAdapterException(
                $"QHY ROI ({settings.RoiX},{settings.RoiY},{roiWidth},{roiHeight}) exceeds binned frame {fullWidth}x{fullHeight}.");
        }
    }

    private static ushort[] ConvertPixels(byte[] buffer, int pixelCount, uint bitsPerPixel)
    {
        var pixels = new ushort[pixelCount];
        if (bitsPerPixel == 16)
        {
            if (buffer.Length < pixelCount * 2) throw new QhyAdapterException("QHY frame buffer is shorter than the 16-bit image geometry.");
            for (var index = 0; index < pixelCount; index++)
            {
                pixels[index] = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(index * 2, 2));
            }
        }
        else if (bitsPerPixel == 8)
        {
            if (buffer.Length < pixelCount) throw new QhyAdapterException("QHY frame buffer is shorter than the 8-bit image geometry.");
            for (var index = 0; index < pixelCount; index++) pixels[index] = (ushort)(buffer[index] * 257);
        }
        else
        {
            throw new QhyAdapterException($"QHY returned unsupported bit depth {bitsPerPixel}.");
        }

        return pixels;
    }

    private static void ValidateExactIdentity(QhyCameraIdentity current, string expectedStableId, string expectedModel)
    {
        if (!string.Equals(current.StableId, expectedStableId, StringComparison.Ordinal) ||
            !string.Equals(current.Model, expectedModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new QhyAdapterException("The connected native QHY device does not match the requested exact identity.");
        }
    }

    private static void ConfigureNativeLibrary(string configuredPath, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new QhyAdapterException("NativeSdkPath must point to a pinned x64 qhyccd.dll before hardware mode can be enabled.");
        }

        var fullPath = Path.GetFullPath(configuredPath);
        if (!File.Exists(fullPath)) throw new QhyAdapterException($"Pinned QHY SDK was not found at '{fullPath}'.");
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            throw new QhyAdapterException("NativeSdkSha256 is required in hardware mode to prevent silent SDK substitution.");
        }

        var actualSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath)));
        if (!string.Equals(actualSha256, expectedSha256.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase))
        {
            throw new QhyAdapterException($"Pinned QHY SDK SHA-256 mismatch for '{fullPath}'.");
        }

        lock (ResolverGate)
        {
            if (resolvedSdkPath is not null)
            {
                if (!string.Equals(resolvedSdkPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new QhyAdapterException("This service process already pinned a different QHY SDK path; restart is required to change it.");
                }

                return;
            }

            NativeLibrary.SetDllImportResolver(
                Assembly.GetExecutingAssembly(),
                (libraryName, _, _) => libraryName.Equals("qhyccd", StringComparison.OrdinalIgnoreCase)
                    ? NativeLibrary.Load(fullPath)
                    : IntPtr.Zero);
            resolvedSdkPath = fullPath;
        }
    }

    private static string NullTerminatedAscii(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, length < 0 ? buffer.Length : length).Trim();
    }

    private static void Check(uint result, string operation)
    {
        if (result != Success) throw new QhyAdapterException($"Failed to {operation}; QHY SDK code 0x{result:X8}.");
    }

    private sealed record NativeFrameResult(uint Result, uint Width, uint Height, uint BitsPerPixel, uint Channels);

    private static class NativeMethods
    {
        private const string Library = "qhyccd";

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint InitQHYCCDResource();

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint ReleaseQHYCCDResource();

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint ScanQHYCCD();

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint GetQHYCCDId(uint index, [Out] byte[] id);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
        internal static extern uint GetQHYCCDModel(string id, [Out] byte[] model);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
        internal static extern IntPtr OpenQHYCCD(string id);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint CloseQHYCCD(IntPtr cameraHandle);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint SetQHYCCDStreamMode(IntPtr cameraHandle, byte mode);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint InitQHYCCD(IntPtr cameraHandle);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint IsQHYCCDControlAvailable(IntPtr cameraHandle, int controlId);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint SetQHYCCDParam(IntPtr cameraHandle, int controlId, double value);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern double GetQHYCCDParam(IntPtr cameraHandle, int controlId);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint GetQHYCCDParamMinMaxStep(
            IntPtr cameraHandle,
            int controlId,
            out double minimum,
            out double maximum,
            out double step);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint SetQHYCCDResolution(IntPtr cameraHandle, uint x, uint y, uint width, uint height);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint GetQHYCCDMemLength(IntPtr cameraHandle);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint ExpQHYCCDSingleFrame(IntPtr cameraHandle);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint SetQHYCCDSingleFrameTimeOut(IntPtr cameraHandle, uint timeoutMilliseconds);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint GetQHYCCDSingleFrame(
            IntPtr cameraHandle,
            out uint width,
            out uint height,
            out uint bitsPerPixel,
            out uint channels,
            [Out] byte[] imageData);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint CancelQHYCCDExposingAndReadout(IntPtr cameraHandle);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint SetQHYCCDBinMode(IntPtr cameraHandle, uint horizontal, uint vertical);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint SetQHYCCDBitsMode(IntPtr cameraHandle, uint bits);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint ControlQHYCCDTemp(IntPtr cameraHandle, double targetTemperature);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint GetQHYCCDSDKVersion(out uint year, out uint month, out uint day, out uint subday);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint GetQHYCCDChipInfo(
            IntPtr cameraHandle,
            out double chipWidthMillimeters,
            out double chipHeightMillimeters,
            out uint imageWidth,
            out uint imageHeight,
            out double pixelWidthMicrometers,
            out double pixelHeightMicrometers,
            out uint bitsPerPixel);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint SetQHYCCDReadMode(IntPtr cameraHandle, uint modeNumber);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint SendOrder2QHYCCDCFW(IntPtr cameraHandle, [In] byte[] order, uint length);

        [DllImport(Library, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
        internal static extern uint GetQHYCCDCFWStatus(IntPtr cameraHandle, [Out] byte[] status);
    }
}
