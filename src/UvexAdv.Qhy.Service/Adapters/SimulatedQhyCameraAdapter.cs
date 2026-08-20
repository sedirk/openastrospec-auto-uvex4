using System.Globalization;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Qhy.Service.Adapters;

public sealed class SimulatedQhyCameraAdapter : IQhyCameraAdapter
{
    private readonly QhyServiceOptions options;
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly object stateGate = new();
    private QhyCameraStatus status = new(false, null, null, null, null, DateTimeOffset.UtcNow);
    private QhyCameraIdentity? identity;
    private IReadOnlyList<string>? replayFiles;
    private int replayIndex;
    private int frameCounter;

    public SimulatedQhyCameraAdapter(QhyServiceOptions options)
    {
        this.options = options;
        QhyServiceConfigurationProof.ValidateFilterPositions(options.NativeFilterPositions, requireConfigured: false);
    }

    public string AdapterName => "simulator";

    public QhyCameraStatus Status
    {
        get
        {
            lock (stateGate) return status;
        }
    }

    public Task<QhyCameraIdentity> ConnectExactAsync(
        string expectedStableId,
        string expectedModel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(expectedStableId, options.ExpectedStableId, StringComparison.Ordinal))
        {
            throw new QhyAdapterException(
                $"Simulator stable-ID mismatch. Configured '{options.ExpectedStableId}', requested '{expectedStableId}'.");
        }

        if (!string.Equals(expectedModel, options.ExpectedModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new QhyAdapterException($"Simulator model mismatch. Configured '{options.ExpectedModel}', requested '{expectedModel}'.");
        }

        identity = new QhyCameraIdentity(expectedStableId, expectedModel, AdapterName, "simulated-1", "simulated", "simulated");
        replayFiles = DiscoverReplayFiles(expectedStableId);
        var filterWheel = InitialFilterWheelStatus();
        lock (stateGate)
        {
            status = new QhyCameraStatus(true, identity, -10, 18, null, DateTimeOffset.UtcNow, filterWheel);
        }

        return Task.FromResult(identity);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            status = new QhyCameraStatus(
                false,
                identity,
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                new QhyFilterWheelStatus(
                    options.NativeFilterPositions.Count > 0,
                    false,
                    null,
                    null,
                    "Simulator camera is disconnected.",
                    DateTimeOffset.UtcNow));
        }

        return Task.CompletedTask;
    }

    public Task<QhyFilterWheelStatus> ReadFilterWheelStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Status.Connected) throw new QhyAdapterException("Simulator camera is not connected.");
        return Task.FromResult(Status.FilterWheel ?? InitialFilterWheelStatus());
    }

    public async Task<QhyFilterWheelStatus> SelectFilterAsync(string filterName, CancellationToken cancellationToken)
    {
        await captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Status.Connected) throw new QhyAdapterException("Simulator camera is not connected.");
            return SelectFilterCore(filterName);
        }
        finally
        {
            captureGate.Release();
        }
    }

    public async Task<QhyFrame> CaptureSingleFrameAsync(QhyFrameSettings settings, CancellationToken cancellationToken)
    {
        await captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentIdentity = identity;
            if (!Status.Connected || currentIdentity is null) throw new QhyAdapterException("Simulator camera is not connected.");
            if (options.NativeFilterPositions.Count > 0)
            {
                var selectedFilter = SelectFilterCore(settings.FilterName);
                settings = settings with
                {
                    FilterName = selectedFilter.FilterName ?? throw new QhyAdapterException(
                        "Simulated filter selection completed without an attested configured name."),
                };
            }
            var started = DateTimeOffset.UtcNow;
            if (options.SimulationDelayMilliseconds > 0)
            {
                await Task.Delay(options.SimulationDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }

            var frame = ShouldReplay() ? ReadReplayFrame(settings, currentIdentity, started) : CreateSyntheticFrame(settings, currentIdentity, started);
            lock (stateGate)
            {
                status = status with
                {
                    TemperatureC = settings.TargetTemperatureC ?? -10,
                    CoolerPowerPercent = settings.TargetTemperatureC is null ? 0 : 18,
                    LastError = null,
                    TimestampUtc = DateTimeOffset.UtcNow,
                };
            }

            return frame;
        }
        finally
        {
            captureGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        captureGate.Dispose();
    }

    private IReadOnlyList<string> DiscoverReplayFiles(string expectedStableId)
    {
        if (string.IsNullOrWhiteSpace(options.ReplayDirectory) || !Directory.Exists(options.ReplayDirectory)) return [];
        var files = Directory.EnumerateFiles(options.ReplayDirectory, "*", SearchOption.AllDirectories)
            .Where(static path => Path.GetExtension(path).Equals(".fit", StringComparison.OrdinalIgnoreCase) ||
                                  Path.GetExtension(path).Equals(".fits", StringComparison.OrdinalIgnoreCase) ||
                                  Path.GetExtension(path).Equals(".fts", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Where(path => IsCompatibleReplay(path, expectedStableId))
            .ToArray();
        return files;
    }

    private QhyFilterWheelStatus InitialFilterWheelStatus()
    {
        var configured = options.NativeFilterPositions
            .OrderBy(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return options.NativeFilterPositions.Count == 0
            ? new QhyFilterWheelStatus(
                false,
                false,
                null,
                null,
                "No simulated filter-wheel position map is configured.",
                DateTimeOffset.UtcNow)
            : new QhyFilterWheelStatus(true, true, configured.Value, configured.Key, null, DateTimeOffset.UtcNow);
    }

    private QhyFilterWheelStatus SelectFilterCore(string filterName)
    {
        if (string.IsNullOrWhiteSpace(filterName) ||
            !options.NativeFilterPositions.TryGetValue(filterName.Trim(), out var position))
        {
            throw new QhyAdapterException(
                $"Requested QHY filter '{filterName}' is not present in the explicit filter-position map; no metadata-only fallback is allowed.");
        }

        var result = new QhyFilterWheelStatus(true, true, position, CanonicalFilterName(position), null, DateTimeOffset.UtcNow);
        lock (stateGate)
        {
            status = status with { FilterWheel = result, TimestampUtc = result.TimestampUtc };
        }
        return result;
    }

    private string CanonicalFilterName(int position) =>
        options.NativeFilterPositions.Single(pair => pair.Value == position).Key;

    private bool ShouldReplay()
    {
        var mode = options.SimulatorMode.Trim();
        if (mode.Equals("Replay", StringComparison.OrdinalIgnoreCase) && replayFiles?.Count == 0)
        {
            throw new QhyAdapterException("Replay mode is selected but no exact-ID compatible FITS files were found.");
        }

        return (mode.Equals("Replay", StringComparison.OrdinalIgnoreCase) || mode.Equals("Auto", StringComparison.OrdinalIgnoreCase)) &&
               replayFiles is { Count: > 0 };
    }

    private QhyFrame ReadReplayFrame(QhyFrameSettings requestedSettings, QhyCameraIdentity currentIdentity, DateTimeOffset started)
    {
        var files = replayFiles ?? throw new InvalidOperationException("Replay files were not initialized.");
        var path = files[replayIndex++ % files.Count];
        var replay = QhyFitsCodec.Read(path);
        return new QhyFrame(
            replay.Width,
            replay.Height,
            replay.Pixels,
            started,
            started.AddSeconds(requestedSettings.ExposureSeconds),
            requestedSettings,
            currentIdentity);
    }

    private QhyFrame CreateSyntheticFrame(QhyFrameSettings settings, QhyCameraIdentity currentIdentity, DateTimeOffset started)
    {
        var fullWidth = Math.Max(64, options.SyntheticWidth / settings.BinningX);
        var fullHeight = Math.Max(64, options.SyntheticHeight / settings.BinningY);
        var width = settings.RoiWidth > 0 ? settings.RoiWidth : fullWidth;
        var height = settings.RoiHeight > 0 ? settings.RoiHeight : fullHeight;
        if (settings.RoiX < 0 || settings.RoiY < 0 || width <= 0 || height <= 0 ||
            settings.RoiX + width > fullWidth || settings.RoiY + height > fullHeight)
        {
            throw new QhyAdapterException(
                $"Simulator ROI ({settings.RoiX},{settings.RoiY},{width},{height}) exceeds binned frame {fullWidth}x{fullHeight}.");
        }

        var pixels = new ushort[checked(width * height)];
        var random = new Random(0x51a700 + Interlocked.Increment(ref frameCounter));
        var transparency = 0.9 + (0.08 * Math.Sin(frameCounter * 0.31));
        var background = 410 + (settings.Offset * 0.08) + (2 * Math.Sin(frameCounter * 0.2));
        for (var index = 0; index < pixels.Length; index++)
        {
            var noise = (random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * 9;
            pixels[index] = (ushort)Math.Clamp(Math.Round(background + noise), 0, ushort.MaxValue);
        }

        var starCount = width > 16 && height > 16 ? Math.Clamp(options.SyntheticStars, 1, 2_000) : 0;
        for (var star = 0; star < starCount; star++)
        {
            var centerX = random.Next(8, width - 8);
            var centerY = random.Next(8, height - 8);
            var sigma = 1.1 + (random.NextDouble() * 1.0);
            var peak = Math.Min(50_000, (1_500 + (random.NextDouble() * 7_500)) * transparency * Math.Sqrt(settings.ExposureSeconds));
            for (var dy = -5; dy <= 5; dy++)
            {
                for (var dx = -5; dx <= 5; dx++)
                {
                    var contribution = peak * Math.Exp(-((dx * dx) + (dy * dy)) / (2 * sigma * sigma));
                    var index = ((centerY + dy) * width) + centerX + dx;
                    pixels[index] = (ushort)Math.Clamp(Math.Round(pixels[index] + contribution), 0, ushort.MaxValue);
                }
            }
        }

        var ended = started.AddSeconds(settings.ExposureSeconds);
        return new QhyFrame(width, height, pixels, started, ended, settings, currentIdentity);
    }

    private static bool IsCompatibleReplay(string path, string expectedStableId)
    {
        try
        {
            var header = QhyFitsCodec.Read(path).Header;
            return header.TryGetValue("CAMERAID", out var cameraId) &&
                   string.Equals(cameraId, expectedStableId, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }
}
