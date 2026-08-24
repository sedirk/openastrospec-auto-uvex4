using System.Reflection;
using System.Runtime.ExceptionServices;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.PlateSolving;
using NINA.PlateSolving.Interfaces;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Supplies immutable QHY-service frames to N.I.N.A.'s native centering loop.
/// It deliberately does not use N.I.N.A.'s imaging mediator: ATR585M remains
/// N.I.N.A.'s imaging camera while QHYminiCam8M remains owned by QHY Service.
/// </summary>
internal sealed class NinaNativeQhyCaptureSolver(
    IImageSolver imageSolver,
    Func<CancellationToken, Task<PlateSolveResult>> captureAndSolve) : ICaptureSolver
{
    public IImageSolver ImageSolver { get; set; } = imageSolver;

    public async Task<PlateSolveResult> Solve(
        CaptureSequence seq,
        CaptureSolverParameter parameter,
        IProgress<PlateSolveProgress> solveProgress,
        IProgress<ApplicationStatus> progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = await captureAndSolve(ct).ConfigureAwait(false);
        solveProgress?.Report(new PlateSolveProgress { PlateSolveResult = result });
        return result;
    }
}

/// <summary>
/// Transparent ITelescopeMediator proxy that gives the observation runner one
/// last durable safety checkpoint before each slew requested by N.I.N.A.'s
/// native CenteringSolver. All non-slew members are forwarded unchanged.
/// </summary>
internal class NinaNativeCenteringTelescopeProxy : DispatchProxy
{
    private ITelescopeMediator? inner;
    private Func<Coordinates, CancellationToken, Func<Task<bool>>, Task<bool>>? guardedSlew;

    internal static ITelescopeMediator Create(
        ITelescopeMediator inner,
        Func<Coordinates, CancellationToken, Func<Task<bool>>, Task<bool>> guardedSlew)
    {
        var proxy = DispatchProxy.Create<ITelescopeMediator, NinaNativeCenteringTelescopeProxy>();
        var state = (NinaNativeCenteringTelescopeProxy)(object)proxy;
        state.inner = inner;
        state.guardedSlew = guardedSlew;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (inner is null || guardedSlew is null)
        {
            throw new InvalidOperationException("The N.I.N.A. native-centering telescope proxy is not initialized.");
        }

        args ??= [];
        if (string.Equals(targetMethod.Name, nameof(ITelescopeMediator.SlewToCoordinatesAsync), StringComparison.Ordinal) &&
            targetMethod.ReturnType == typeof(Task<bool>) &&
            args.FirstOrDefault() is Coordinates coordinates)
        {
            var cancellationToken = args.OfType<CancellationToken>().FirstOrDefault();
            return guardedSlew(
                coordinates,
                cancellationToken,
                () => InvokeSlewAsync(targetMethod, args));
        }

        try
        {
            return targetMethod.Invoke(inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private async Task<bool> InvokeSlewAsync(MethodInfo targetMethod, object?[] args)
    {
        try
        {
            return await ((Task<bool>)targetMethod.Invoke(inner, args)!).ConfigureAwait(false);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}

internal sealed class NinaNativeCenteringException(
    string code,
    string message,
    string? evidencePath = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public string? EvidencePath { get; } = evidencePath;
}
