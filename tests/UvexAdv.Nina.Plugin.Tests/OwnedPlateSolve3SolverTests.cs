using System.Diagnostics;
using System.Security.Cryptography;
using NINA.Astrometry;
using NINA.Image.FileFormat;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class OwnedPlateSolve3SolverTests
{
    [Fact]
    public async Task ActualIsolatedEngineSolvesRecordedQhyFrameThroughOwnedAdapter()
    {
        var folder = Environment.GetEnvironmentVariable("UVEX_PL3_OWNED_REPLAY");
        if (string.IsNullOrWhiteSpace(folder)) return;
        var sourcePath = Path.Combine(folder, "solver-input.fit");
        var before = SHA256.HashData(File.ReadAllBytes(sourcePath));
        var input = StrictWorkflowPort.Create<IImageData>(out var trace);
        trace.Handlers["get_Properties"] = _ => new ImageProperties(3856, 2180, 16, false, 0, 0);
        string? generated = null;
        trace.Handlers["SaveToDisk"] = args =>
        {
            var info = (FileSaveInfo)args[0]!;
            generated = Path.Combine(info.FilePath, info.FilePattern + ".fits");
            File.Copy(sourcePath, generated);
            return Task.FromResult(generated);
        };
        var result = await new OwnedPlateSolve3Solver(Path.Combine(folder, "isolated-solver", "PlateSolve3.80.exe"))
            .SolveAsync(input, new PlateSolveParameter { FocalLength = 350, PixelSize = 2.9, Binning = 2,
                Coordinates = new Coordinates(53.44716399675508, 48.13095475143502, Epoch.J2000, Coordinates.RAType.Degrees) }, null!, default);
        Assert.True(result.Success);
        Assert.InRange(result.Coordinates.RADegrees, 53.442, 53.444);
        Assert.InRange(result.Coordinates.Dec, 48.131, 48.133);
        Assert.InRange(result.Pixscale, 1.70, 1.72);
        Assert.NotNull(generated);
        Assert.False(File.Exists(generated));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(sourcePath)));
    }

    private static ProcessStartInfo Sleep() => new("powershell.exe")
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" },
    };

    [Fact]
    public async Task PreemptedSolveJoinsOnlyItsOwnChildBeforeReturningCancellation()
    {
        using var other = Process.Start(Sleep())!;
        using var token = new CancellationTokenSource();
        var pid = 0;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OwnedPlateSolve3Solver.RunChildAsync(
                Sleep(), TimeSpan.FromSeconds(20), token.Token, id => { pid = id; token.Cancel(); }));
            Assert.True(Exited(pid));
            Assert.False(other.HasExited);
            // A subsequent foreground child really executes after preemption.
            var next = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "exit 7" } };
            Assert.Equal(7, await OwnedPlateSolve3Solver.RunChildAsync(next, TimeSpan.FromSeconds(10), default));
        }
        finally { if (!other.HasExited) { other.Kill(true); await other.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task StuckChildOrModalTimesOutAndIsJoined()
    {
        var pid = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => OwnedPlateSolve3Solver.RunChildAsync(
            Sleep(), TimeSpan.FromMilliseconds(200), default, id => pid = id));
        Assert.True(Exited(pid));
    }

    [Fact]
    public async Task AlreadyCancelledDoesNotStartAnyProcess()
    {
        var started = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OwnedPlateSolve3Solver.RunChildAsync(
            Sleep(), TimeSpan.FromSeconds(1), new CancellationToken(true), _ => started = true));
        Assert.False(started);
    }

    [Theory]
    [InlineData("True")]
    [InlineData("True\n1\n3")]
    [InlineData("True\n1,0.2\nNaN,0")]
    [InlineData("True\n1,0.2\n-1,0")]
    [InlineData("True\n1,2\n100,0")]
    public void MalformedNativeOutputCannotBecomeWcsOrArrayException(string text) =>
        Assert.Throws<IOException>(() => OwnedPlateSolve3Solver.ParseResult(text.Split('\n'), 1920, 1080));

    [Fact]
    public void NativeConventionAndOrdinaryFailedSolveArePreserved()
    {
        Assert.False(OwnedPlateSolve3Solver.ParseResult(["False"], 1920, 1080).Success);
        var result = OwnedPlateSolve3Solver.ParseResult(["True", "1,0.2", "537148,12.5"], 1920, 1080);
        Assert.True(result.Success);
        Assert.Equal(180 / Math.PI, result.Coordinates.RADegrees, 8);
        Assert.Equal(206264.8 / 537148, result.Pixscale, 9);
        Assert.Equal(12.5, result.PositionAngle);
        Assert.False(result.Flipped); // PL3 parity convention remains in G3WcsTargetProjector.
    }

    private static bool Exited(int pid)
    {
        Assert.True(pid > 0);
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
}
