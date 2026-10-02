using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace UvexAdv.Spectroscopy;

public sealed record ReductionPreviewResult(int Schema, string Algorithm, bool PreviewOnly, bool Calibrated,
    bool SpectralSmoothing, string Backend, double?[] Flux, double?[] RawFlux, double?[] Uncertainty,
    int CosmicPixels, int MaskedColumns, string TraceMethod, string[] Warnings);

/// <summary>Bounded image-only process. Never opens a device or sends a raw file path.</summary>
public static class ReductionPreviewClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string FindPython(string localAppData)
    {
        var explicitPath = Path.Combine(localAppData, "UVEX-ADV", "reduction-preview-runtime.json");
        if (File.Exists(explicitPath))
        {
            using var config = ReadSmallConfig(explicitPath);
            return RequirePython(config.RootElement.GetProperty("PythonPath").GetString());
        }
        // The installed post-processing launcher already records the exact pinned
        // venv location. No PATH search, package install or dependency upgrade.
        var launcher = Path.Combine(localAppData, "Programs", "UVEX-ADV", "Reduction", "launcher.settings.json");
        using var settings = ReadSmallConfig(launcher);
        var root = settings.RootElement.GetProperty("ProjectRoot").GetString();
        if (root is null || !Path.IsPathFullyQualified(root)) throw new InvalidDataException("Invalid reducer installation.");
        return RequirePython(Path.Combine(root, "reduction", ".venv", "Scripts", "python.exe"));
    }

    private static JsonDocument ReadSmallConfig(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 16384)
            throw new InvalidOperationException("后期运行环境未安装；请安装光谱处理软件或配置 reduction-preview-runtime.json。");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string RequirePython(string? path) =>
        path is not null && Path.IsPathFullyQualified(path) && File.Exists(path)
            ? path : throw new InvalidOperationException("后期 Python 路径无效；未改用系统 Python。");

    public static async Task<ReductionPreviewResult> ExtractAsync(string python, string worker,
        ushort[] pixels, int width, int height, double saturation, double center, double halfWidth,
        CancellationToken token, TimeSpan? timeout = null)
    {
        RequirePython(python);
        if (!Path.IsPathFullyQualified(worker) || !File.Exists(worker)) throw new FileNotFoundException("Missing bundled reduction worker.");
        if (width < 32 || height < 32 || (long)width * height > 32_000_000 || pixels.LongLength != (long)width * height ||
            !double.IsFinite(saturation) || saturation <= 0 || !double.IsFinite(center) || center < 0 || center >= height ||
            !double.IsFinite(halfWidth) || halfWidth is < 2 or > 128)
            throw new ArgumentException("Invalid preview image or trace.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-E"); start.ArgumentList.Add("-s"); start.ArgumentList.Add("-B"); start.ArgumentList.Add(worker);
        start.Environment["OMP_NUM_THREADS"] = "2";
        start.Environment["OPENBLAS_NUM_THREADS"] = "2";
        using var process = Process.Start(start) ?? throw new IOException("Cannot start reduction preview.");
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, 4_000_000, ct);
            var errors = ReadBoundedAsync(process.StandardError, 65_536, ct);
            var input = WriteAsync(process.StandardInput.BaseStream);
            var io = new Task[] { input, output, errors };
            foreach (var task in io)
                _ = task.ContinueWith(_ => deadline.Cancel(), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await Task.WhenAll(io).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidDataException("后期提取失败：" + (await errors.ConfigureAwait(false))[^Math.Min(500, errors.Result.Length)..]);
            return ParseAndValidate(await output.ConfigureAwait(false), width);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true); // Only our image-only child.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        async Task WriteAsync(Stream stream)
        {
            await using (stream.ConfigureAwait(false))
            {
                var header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                { schema = 1, width, height, saturation, traceCenter = center, traceHalfWidth = halfWidth }) + "\n");
                await stream.WriteAsync(header, ct).ConfigureAwait(false);
                var buffer = new byte[65536];
                for (var offset = 0; offset < pixels.Length;)
                {
                    var count = Math.Min(buffer.Length / 2, pixels.Length - offset);
                    for (var i = 0; i < count; i++)
                    {
                        buffer[i * 2] = (byte)pixels[offset + i];
                        buffer[i * 2 + 1] = (byte)(pixels[offset + i] >> 8);
                    }
                    await stream.WriteAsync(buffer.AsMemory(0, count * 2), ct).ConfigureAwait(false);
                    offset += count;
                }
            }
        }
    }

    public static ReductionPreviewResult ParseAndValidate(string json, int width)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("calibrated", out var calibrated) || calibrated.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("spectralSmoothing", out var smoothing) || smoothing.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Preview cannot claim calibration or smoothing.");
        var result = JsonSerializer.Deserialize<ReductionPreviewResult>(json, Json) ?? throw new InvalidDataException("Empty preview.");
        if (result.Schema != 1 || result.Algorithm != "reduction-live-preview-v1" || !result.PreviewOnly ||
            result.Calibrated || result.SpectralSmoothing || string.IsNullOrWhiteSpace(result.Backend) ||
            result.CosmicPixels < 0 || result.MaskedColumns < 0 || result.MaskedColumns > width || result.Warnings is null ||
            result.Flux is null || result.RawFlux is null || result.Uncertainty is null)
            throw new InvalidDataException("Invalid preview contract.");
        foreach (var array in new[] { result.Flux, result.RawFlux, result.Uncertainty })
            if (array.Length != width || array.Any(v => v.HasValue && !double.IsFinite(v.Value)))
                throw new InvalidDataException("Invalid preview samples.");
        if (result.Flux.Count(v => v.HasValue) < width * .5 || result.Uncertainty.Any(v => v is <= 0))
            throw new InvalidDataException("Insufficient preview coverage.");
        return result;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (result.Length + count > limit) throw new InvalidDataException("Oversized preview response.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
