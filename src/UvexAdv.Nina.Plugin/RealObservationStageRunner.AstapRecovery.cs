using System.IO;
using NINA.Astrometry;
using NINA.PlateSolving;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private readonly MeasuredAstapRecovery measuredAstapRecovery = new();

    private async Task<MeasuredAstapAttempt?> TryMeasuredAstapAsync(PlateSolveResult original, string role,
        string source, string hash, int width, int height, Coordinates hint, double scale, CancellationToken token)
    {
        if (!measuredAstapRecovery.Claim(role, solverIdentity, original.Success, hash)) return null;
        if (observationRunId is null) return null;
        Report("PL3 本帧未解出：在原预算内补试一次实测提星 / ASTAP，并回到原片核验星点；不重新曝光。");
        // Same pending-motion envelope as the primary solve. No timer, return
        // reserve, action count or capture ownership is created/reset here.
        return await RunWithG3PendingReturnTimeAsync("measured-astap-recovery", ct =>
            RunWithImageSolverPriorityAsync(role, async solverToken =>
            {
                try
                {
                    return await measuredAstapRecovery.SolveAsync(source, hash, width, height, hint, scale,
                        configuration.PlateSolver.SearchRadiusDegrees, Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(G3AcquisitionMotionPath(observationRunId)))!, "analysis"), solverToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException &&
                    !ex.Message.Contains("CHILD_NOT_JOINED", StringComparison.Ordinal) &&
                    !ex.Message.Contains("SOURCE_CHANGED", StringComparison.Ordinal))
                {
                    // Optional analysis errors do not turn an ordinary unsolved
                    // target into a new night-ending exception.
                    return new MeasuredAstapAttempt(null, ex.Message, null);
                }
            }, ct), token).ConfigureAwait(false);
    }
}
