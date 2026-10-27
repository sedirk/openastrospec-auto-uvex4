using System.Diagnostics;
using System.Globalization;
using System.IO;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Image.FileFormat;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Same configured PL3 engine/CLI/result convention as NINA 3.2, with a joined
/// child lifetime. NINA's CLISolver cancels WaitForExitAsync without terminating
/// the child, allowing a preempted keyframe solver to outlive its input and gate.
/// This adapter touches only its own disposable input and the child it started.
/// </summary>
internal sealed class OwnedPlateSolve3Solver(string executable) : IPlateSolver
{
    private static readonly SemaphoreSlim processGate = new(1, 1);
    private static int unjoinedChild;
    private static readonly string inputDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OpenAstroSpec", "owned-plate-solve"));
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public async Task<PlateSolveResult> SolveAsync(IImageData source, PlateSolveParameter parameter,
        IProgress<ApplicationStatus> progress, CancellationToken canceltoken)
    {
        await processGate.WaitAsync(canceltoken).ConfigureAwait(false);
        string? imagePath = null, outputPath = null;
        var retainDisposableInput = false;
        try
        {
            canceltoken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref unjoinedChild) != 0) throw new IOException("PL3_CHILD_NOT_JOINED: Further plugin solves are disabled until restart; no new child started.");
            if (!File.Exists(executable)) throw new FileNotFoundException("Configured PlateSolve3 executable is unavailable.", executable);
            if (parameter.FocalLength <= 0 || parameter.PixelSize <= 0 ||
                !double.IsFinite(parameter.FocalLength) || !double.IsFinite(parameter.PixelSize))
                throw new ArgumentException("Finite commissioned solver optics are required.");
            Directory.CreateDirectory(inputDirectory);
            imagePath = await source.SaveToDisk(new FileSaveInfo
            {
                FilePath = inputDirectory, FilePattern = Guid.NewGuid().ToString("N"), FileType = FileTypeEnum.FITS,
            }, canceltoken, forceFileType: true).ConfigureAwait(false);
            if (!IsDisposable(imagePath)) throw new IOException("PL3_INPUT_OUTSIDE_TEMP: Refusing unexpected solver input location.");
            outputPath = Path.Combine(Path.GetDirectoryName(imagePath)!, Path.GetFileNameWithoutExtension(imagePath)) + "_PS3.txt";
            var scale = AstroUtil.ArcsecPerPixel(parameter.PixelSize, parameter.FocalLength);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(imagePath);
            start.ArgumentList.Add(Number(parameter.Coordinates is null ? 0 : AstroUtil.ToRadians(parameter.Coordinates.RADegrees)));
            start.ArgumentList.Add(Number(parameter.Coordinates is null ? 0 : AstroUtil.ToRadians(parameter.Coordinates.Dec)));
            start.ArgumentList.Add(Number(AstroUtil.ToRadians(scale * source.Properties.Width / 3600)));
            start.ArgumentList.Add(Number(AstroUtil.ToRadians(scale * source.Properties.Height / 3600)));
            progress?.Report(new ApplicationStatus { Status = "PlateSolve3：有界解析；取消后确认本次解析进程退出" });
            // A stuck external modal must not outlive the original caller's
            // cancellation/return reserve. This cap only shortens a solve.
            var exit = await RunChildAsync(start, TimeSpan.FromSeconds(60), canceltoken).ConfigureAwait(false);
            if (exit != 0) throw new IOException($"PL3_CHILD_FAILED: Owned solver exited with code {exit}; no WCS accepted.");
            if (!File.Exists(outputPath)) throw new IOException("PL3_RESULT_MISSING: Owned solver exited without a result; not a completed failed optical solve.");
            return ParseResult(await File.ReadAllLinesAsync(outputPath, canceltoken).ConfigureAwait(false),
                source.Properties.Width, source.Properties.Height);
        }
        catch (ChildJoinException)
        {
            retainDisposableInput = true;
            Interlocked.Exchange(ref unjoinedChild, 1);
            throw;
        }
        finally
        {
            // Never a source observation path: only the unique SaveToDisk
            // product and its deterministic native sidecar, after child join.
            if (!retainDisposableInput)
            {
                DeleteDisposable(imagePath);
                DeleteDisposable(outputPath);
            }
            try { progress?.Report(new ApplicationStatus { Status = string.Empty }); }
            finally { processGate.Release(); }
        }
    }

    internal static async Task<int> RunChildAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken token,
        Action<int>? started = null)
    {
        token.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("PL3_CHILD_START_FAILED");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(timeout);
        try
        {
            started?.Invoke(process.Id);
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("PL3_CHILD_TIMEOUT: Bounded solver did not finish; no WCS accepted.");
        }
        finally
        {
            if (!process.HasExited)
            {
                // Exact owned analysis child only; never enumerate/kill NINA,
                // PHD2, services, equipment owners or another solver instance.
                try
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    using var join = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(join.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { throw new ChildJoinException($"PL3_CHILD_NOT_JOINED: Owned analysis PID {process.Id} could not be confirmed exited.", ex); }
            }
        }
    }

    internal static PlateSolveResult ParseResult(string[] lines, int width, int height)
    {
        if (lines.Length == 0 || !bool.TryParse(lines[0].Trim(), out var success)) throw new IOException("PL3_RESULT_INVALID: Missing result status.");
        if (!success) return new PlateSolveResult { Success = false };
        if (lines.Length < 3) throw new IOException("PL3_RESULT_INVALID: Truncated successful result.");
        var sky = lines[1].Split(',');
        var geometry = lines[2].Split(',');
        if (sky.Length < 2 || geometry.Length < 2 || !Try(sky[0], out var ra) || !Try(sky[1], out var dec) ||
            !Try(geometry[0], out var inverseScale) || inverseScale <= 0 || !Try(geometry[1], out var angle) ||
            ra < 0 || ra > 2 * Math.PI || Math.Abs(dec) > Math.PI / 2)
            throw new IOException("PL3_RESULT_INVALID: Nonfinite or incomplete sky geometry.");
        var scale = 206264.8 / inverseScale;
        var radius = AstroUtil.ArcsecToDegree(Math.Sqrt(Math.Pow(width * scale, 2) + Math.Pow(height * scale, 2)) / 2);
        if (!double.IsFinite(scale) || !double.IsFinite(radius)) throw new IOException("PL3_RESULT_INVALID: Unbounded result scale.");
        return new PlateSolveResult
        {
            Success = true, Coordinates = new Coordinates(AstroUtil.ToDegree(ra), AstroUtil.ToDegree(dec), Epoch.J2000, Coordinates.RAType.Degrees),
            Pixscale = scale, PositionAngle = angle,
            Radius = radius,
        };
    }

    private static bool Try(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
    private static void DeleteDisposable(string? path)
    {
        if (path is null || !IsDisposable(path)) return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static bool IsDisposable(string path) => Path.GetFullPath(path).StartsWith(inputDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed class ChildJoinException(string message, Exception inner) : IOException(message, inner);
}
