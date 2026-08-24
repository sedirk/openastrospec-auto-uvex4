using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NinaNativeCenteringSafetyTests
{
    private static readonly string AdapterSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "NinaNativeQhyCentering.cs"));
    private static readonly string RunnerSource = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Sources",
        "RealObservationStageRunner.NinaNativeCentering.cs"));

    [Fact]
    public void NativeCenteringReusesNinaLoopWithoutChangingCameraOwnership()
    {
        Assert.Contains("NINA.PlateSolving.CenteringSolver", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("plateSolverFactory.GetCenteringSolver(", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("solver.CaptureSolver = new NinaNativeQhyCaptureSolver", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("var reacquired = await AcquireQhyWideFieldAsync", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("NoSync = true", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("RequireImmediatePhysicalActionGatesAsync", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("ValidateQhyAcceptedFrameMountBindingForMotionAsync", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("ValidateQhyCoarseMoveAndReturnReserve", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("StopQhyCoarseAndReturnAsync", RunnerSource, StringComparison.Ordinal);

        Assert.DoesNotContain("ICameraMediator", AdapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IImagingMediator", AdapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("QhyServiceClient", AdapterSource, StringComparison.Ordinal);
        Assert.DoesNotContain("COM5", AdapterSource, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeRequestedSlewIsInterceptedAndSegmentedByOwnerGuard()
    {
        Assert.Contains("NinaNativeCenteringTelescopeProxy.Create", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("Math.Min(fullMagnitude, limits.MaximumSingleCorrectionArcseconds)", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("telescopeMediator.SlewToCoordinatesAsync(boundedCoordinate", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("MountCommandArrivalToleranceArcseconds", RunnerSource, StringComparison.Ordinal);
        Assert.Contains("nameof(ITelescopeMediator.SlewToCoordinatesAsync)", AdapterSource, StringComparison.Ordinal);
        Assert.Contains("return guardedSlew(", AdapterSource, StringComparison.Ordinal);
    }
}
