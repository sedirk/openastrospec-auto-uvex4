using UvexAdv.Core;
using UvexAdv.Spectroscopy;

namespace UvexAdv.Nina.Plugin;

internal sealed class NinaClosedLoopContext(
    UvexServiceClient service,
    UvexServiceClient.UvexLeaseSession lease,
    NinaSpectrumCapture capture,
    UvexDeviceStatus initialStatus) : ISpectralFocusContext, IWavelengthLockContext
{
    public int CurrentFocusPositionSteps { get; private set; } =
        initialStatus.FocusPositionSteps ?? throw new InvalidOperationException("UVEX focus position is unknown.");

    public int CurrentGratingPositionSteps { get; private set; } =
        initialStatus.GratingPositionSteps ?? throw new InvalidOperationException("UVEX grating position is unknown.");

    public async Task MoveFocusToAsync(int positionSteps, CancellationToken cancellationToken)
    {
        var delta = checked(positionSteps - CurrentFocusPositionSteps);
        if (delta == 0)
        {
            return;
        }

        var operation = await service.MoveFocusAsync(delta, lease.Token, cancellationToken).ConfigureAwait(false);
        await service.WaitForOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        CurrentFocusPositionSteps = positionSteps;
    }

    public async Task MoveGratingRelativeAsync(int deltaSteps, CancellationToken cancellationToken)
    {
        if (deltaSteps == 0)
        {
            return;
        }

        var operation = await service.MoveGratingAsync(deltaSteps, lease.Token, cancellationToken).ConfigureAwait(false);
        await service.WaitForOperationAsync(operation, cancellationToken).ConfigureAwait(false);
        CurrentGratingPositionSteps = checked(CurrentGratingPositionSteps + deltaSteps);
    }

    public Task<Spectrum1D> CaptureSpectrumAsync(CancellationToken cancellationToken) => capture.CaptureAsync(cancellationToken);
}
