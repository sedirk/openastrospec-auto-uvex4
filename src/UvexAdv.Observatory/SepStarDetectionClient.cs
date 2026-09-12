using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UvexAdv.Observatory;

public sealed record SepDetectionRuntime(string PythonPath, string WorkerPath);
public sealed record SepPositionComponent(double X, double Y, double A, double B, double Flux,
    double Peak, double Snr, double SaturatedFraction, int RawSupportPixels, int Npix,
    int[] Bbox, int SepFlags, int ParentId);
public sealed record SepMeasuredSource(double X, double Y, double? R50, double? R80,
    double Flux, double Peak, double Background, double Snr, int[] Bbox,
    string[] Flags, bool FocusEligible)
{
    // Preserve SEP moments, children, support and aperture flags for diagnostics.
    [JsonExtensionData] public Dictionary<string, JsonElement>? Details { get; init; }
}
public sealed record SepImageMeasurements(int Schema, string Algorithm, int Width, int Height,
    SepMeasuredSource[] Sources, bool TargetIdentityConfirmed, bool MotionAuthorized)
{
    public SepPositionComponent[]? Components { get; init; }
    public bool ComponentsTruncated { get; init; }
    public SepPositionComponent[]? ParentComponents { get; init; }
    public bool ParentComponentsTruncated { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Details { get; init; }
}

/// <summary>Optional image-only worker. Owns no device and writes no raw files.</summary>
public sealed class SepStarDetectionClient
{
    public const string Algorithm = "sep-parent-energy-v3";
    private static readonly SemaphoreSlim WorkerSlot = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static SepDetectionRuntime ReadRuntime(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 16384)
            throw new InvalidOperationException("SEP_RUNTIME_NOT_CONFIGURED: " + path);
        var runtime = JsonSerializer.Deserialize<SepDetectionRuntime>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("SEP runtime configuration is empty.");
        if (string.IsNullOrWhiteSpace(runtime.PythonPath) || string.IsNullOrWhiteSpace(runtime.WorkerPath)
            || !Path.IsPathFullyQualified(runtime.PythonPath) || !Path.IsPathFullyQualified(runtime.WorkerPath)
            || !File.Exists(runtime.PythonPath) || !File.Exists(runtime.WorkerPath))
            throw new InvalidDataException("SEP runtime requires existing absolute Python and worker paths.");
        return runtime;
    }

    public async Task<SepImageMeasurements> DetectAsync(SepDetectionRuntime runtime,
        int width, int height, ushort[] rawPixels, double saturation, CancellationToken token)
        => await DetectCoreAsync(runtime, width, height, rawPixels, saturation, null, token).ConfigureAwait(false);

    public async Task<SepFocusFrame> MeasureFocusAsync(SepDetectionRuntime runtime,
        int width, int height, ushort[] rawPixels, double saturation, SepFocusReference[]? reference, CancellationToken token)
    {
        var image = await DetectCoreAsync(runtime, width, height, rawPixels, saturation,
            new { reference }, token).ConfigureAwait(false);
        if (image.Details is null || !image.Details.TryGetValue("focus", out var payload))
            throw new InvalidDataException("SEP focus worker missing; install the updated optional runtime.");
        var frame = payload.Deserialize<SepFocusFrame>(JsonOptions) ?? throw new InvalidDataException("Empty focus result");
        frame.Validate(width, height);
        return frame;
    }

    private async Task<SepImageMeasurements> DetectCoreAsync(SepDetectionRuntime runtime,
        int width, int height, ushort[] rawPixels, double saturation, object? focusRequest, CancellationToken token)
    {
        if (width < 16 || height < 16 || (long)width * height > 32_000_000
            || rawPixels.LongLength != (long)width * height || !double.IsFinite(saturation) || saturation <= 0)
            throw new ArgumentException("Invalid raw monochrome image dimensions or saturation.");
        await WorkerSlot.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var ct = deadline.Token;
            var start = new ProcessStartInfo(runtime.PythonPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add("-E"); // Ignore PYTHONPATH/PYTHONHOME; use the explicitly configured venv.
            start.ArgumentList.Add("-s");
            start.ArgumentList.Add(runtime.WorkerPath);
            using var process = Process.Start(start) ?? throw new IOException("SEP worker could not start.");
            try
            {
                var outputTask = ReadBoundedAsync(process.StandardOutput, 4_000_000, ct);
                var errorTask = ReadBoundedAsync(process.StandardError, 16_384, ct);
                // Any failed pipe immediately cancels the other pipes (no 60-second deadlock).
                var writeTask = WriteImageAsync(process.StandardInput.BaseStream, width, height, rawPixels, saturation, focusRequest, ct);
                var io = new Task[] { outputTask, errorTask, writeTask };
                foreach (var task in io)
                    _ = task.ContinueWith(_ => deadline.Cancel(), CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                await Task.WhenAll(io).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("SEP_WORKER_FAILED: " + await errorTask.ConfigureAwait(false));
                return ParseAndValidate(await outputTask.ConfigureAwait(false), width, height);
            }
            finally
            {
                // Only the child created by this call. Never stop NINA/PHD2/services.
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { WorkerSlot.Release(); }
    }

    public static SepImageMeasurements ParseAndValidate(string json, int width, int height)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("motion_authorized", out var motion) || motion.ValueKind != JsonValueKind.False
            || !root.TryGetProperty("target_identity_confirmed", out var identity) || identity.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("SEP must explicitly remain measurement-only.");
        var result = JsonSerializer.Deserialize<SepImageMeasurements>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty SEP result.");
        if (result.Schema != 1 || result.Algorithm != Algorithm || result.Width != width || result.Height != height
            || result.MotionAuthorized || result.TargetIdentityConfirmed || result.Sources is null || result.Sources.Length > 2000)
            throw new InvalidDataException("Invalid SEP measurement contract.");
        foreach (var star in result.Sources)
        {
            if (star is null || !double.IsFinite(star.X) || !double.IsFinite(star.Y) || star.X < 0 || star.Y < 0 || star.X >= width || star.Y >= height
                || !double.IsFinite(star.Flux) || !double.IsFinite(star.Peak) || !double.IsFinite(star.Background) || !double.IsFinite(star.Snr)
                || star.Flags is null || star.Bbox is not { Length: 4 }
                || star.Bbox[0] < 0 || star.Bbox[1] < 0 || star.Bbox[2] < 1 || star.Bbox[3] < 1
                || (long)star.Bbox[0] + star.Bbox[2] > width || (long)star.Bbox[1] + star.Bbox[3] > height
                || (star.FocusEligible && (star.Flags.Length != 0 || star.R50 is not > 0 || star.R80 is not > 0
                    || !double.IsFinite(star.R50.Value) || !double.IsFinite(star.R80.Value) || star.R50 > star.R80)))
                throw new InvalidDataException("SEP source has invalid coordinates, aperture or focus flags.");
        }
        if (result.Components is null || result.Components.Length > 2000
            || !root.TryGetProperty("components_truncated", out var truncated)
            || truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("SEP component coverage is missing.");
        if (result.ParentComponents is null || result.ParentComponents.Length > 2000
            || !root.TryGetProperty("parent_components_truncated",out var parentsTruncated)
            || parentsTruncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("SEP parent coverage is missing.");
        foreach (var c in result.Components.Concat(result.ParentComponents))
        {
            if (c is null || !double.IsFinite(c.X) || !double.IsFinite(c.Y)
                || c.X < 0 || c.Y < 0 || c.X >= width || c.Y >= height
                || !double.IsFinite(c.A) || !double.IsFinite(c.B) || c.A <= 0 || c.B <= 0 || c.B > c.A
                || !double.IsFinite(c.Flux) || !double.IsFinite(c.Peak) || !double.IsFinite(c.Snr)
                || !double.IsFinite(c.SaturatedFraction) || c.SaturatedFraction is < 0 or > 1
                || c.Npix < 9 || c.RawSupportPixels < 3 || c.RawSupportPixels > c.Npix
                || c.SepFlags < 0 || c.ParentId < 0 || c.Bbox is not { Length: 4 }
                || c.Bbox[0] < 0 || c.Bbox[1] < 0 || c.Bbox[2] < 1 || c.Bbox[3] < 1
                || (long)c.Bbox[0] + c.Bbox[2] > width || (long)c.Bbox[1] + c.Bbox[3] > height
                || c.X < c.Bbox[0] || c.X > c.Bbox[0] + c.Bbox[2] - 1
                || c.Y < c.Bbox[1] || c.Y > c.Bbox[1] + c.Bbox[3] - 1)
                throw new InvalidDataException("Invalid SEP component measurement.");
        }
        return result;
    }

    private static async Task WriteImageAsync(Stream stream, int width, int height, ushort[] pixels, double saturation, object? focusRequest, CancellationToken ct)
    {
        await using (stream.ConfigureAwait(false))
        {
            var header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { schema = 1, width, height, saturation, focus_request = focusRequest }, JsonOptions) + "\n");
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            var bytes = new byte[65536];
            for (var offset = 0; offset < pixels.Length;)
            {
                var count = Math.Min(bytes.Length / 2, pixels.Length - offset);
                for (var i = 0; i < count; i++)
                {
                    var value = pixels[offset + i];
                    bytes[2 * i] = (byte)value;
                    bytes[2 * i + 1] = (byte)(value >> 8);
                }
                await stream.WriteAsync(bytes.AsMemory(0, 2 * count), ct).ConfigureAwait(false);
                offset += count;
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumChars, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (text.Length + count > maximumChars) throw new InvalidDataException("SEP output exceeds protocol limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
