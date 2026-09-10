using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private async Task<T> RunWithG3PendingReturnTimeAsync<T>(string operation,
        Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var state = durableG3AcquisitionMotion;
        if (state is { Phase: G3AcquisitionMotionPhase.AwaitingFreshSolve })
            state = ReanchorG3AcquisitionMotionFromReportedPosition(state, telescopeMediator.GetCurrentPosition());
        var plan = G3AcquisitionReturnTimePolicy.Plan(state, DateTimeOffset.UtcNow);
        try
        {
            return await G3AcquisitionReturnTimePolicy.ExecuteAsync(plan, work, cancellationToken).ConfigureAwait(false);
        }
        catch (G3ReturnTimeReserveException ex)
        {
            if (operation == "capture" && ex.OperationStarted && ex.Gate.Code == G3AcquisitionReturnTimePolicy.ReserveCode)
            {
                // The capture owner has completed its cancellation cleanup.
                // Confirm stopped before the ordinary guarded mount return.
                using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cleanup.CancelAfter(TimeSpan.FromSeconds(G3AcquisitionReturnTimePolicy.CleanupReserveSeconds));
                if (phd2.Snapshot.AppState is not (Phd2AppState.Stopped or Phd2AppState.Selected or Phd2AppState.Looping))
                    throw new InvalidOperationException("G3_CAPTURE_RETURN_STOP_UNCONFIRMED: PHD2 is no longer in this acquisition's stopped/preview state; no unrelated guide or calibration session was stopped.");
                var stopped = await phd2.StopCaptureAndConfirmAsync(cleanup.Token).ConfigureAwait(false);
                ValidateConfirmedPhdStop(stopped, "G3 capture return-time reservation");
            }
            Report("导星相机取图/解算已停止：为原有分段回程保留时间，准备按原运动账本返回。");
            await WriteAuditBestEffortAsync("g3-work-stopped-for-return-reserve",
                new { operation, gate = ex.Gate, budgetLineageId = state?.BudgetLineageId, budgetReset = false }).ConfigureAwait(false);
            throw;
        }
    }
}
