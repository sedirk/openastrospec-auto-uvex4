using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private readonly NinaRoofCloseBoundary roofCloseBoundary = new();
    private NinaRoofConnectionBoundary RoofConnection => NinaRoofConnectionBoundary.For(domeMediator);
    private string BoundRoofId => configuration.Environment.Devices.DomeOrRoofId;
    private TimeSpan RoofTransitionLimit => TimeSpan.FromSeconds(configuration.Environment.DomeOrRoofTransitionTimeoutSeconds);

    private GateResult? CurrentRoofBoundaryGate()
    {
        if (NightStartHomeDriverBoundary.For(telescopeMediator).Unjoined is { } homeTask) return homeTask;
        if (RoofConnection.PendingGate is { } pending) return pending;
        var selection = ValidateCurrentEnvironmentDeviceSelections();
        return selection.Disposition == GateDisposition.Passed ? null : selection;
    }

    private async Task<GateResult> CheckInitialRoofReadbackAsync(CancellationToken token)
    {
        GateResult Read() => CurrentRoofBoundaryGate() ??
            RealObservationStageRunnerFactory.CheckStandbyRoofReadback(domeMediator.GetInfo(), BoundRoofId);
        var first = domeMediator.GetInfo();
        // Standby and startup both wait for the existing native poll. No new COM
        // read/poller, and do not invent freshness from a repeated cached sample.
        var firstState = first.ShutterStatus.ToString();
        var initiallyConnected = first.Connected;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var ready = await RealObservationStageRunnerFactory.WaitForStandbyReadbackAsync(Read, token).ConfigureAwait(false);
        var last = domeMediator.GetInfo();
        var result = CurrentRoofBoundaryGate() ?? NinaRoofReadback.CheckObserving(last, BoundRoofId, allowClosed: true);
        if (ready.Disposition != GateDisposition.Passed && result.Disposition == GateDisposition.Passed) result = ready;
        await WriteAuditBestEffortAsync("initial-roof-readback", new
        {
            firstState, initiallyConnected, lastState = last.ShutterStatus.ToString(), last.Connected, last.Slewing,
            expected = BoundRoofId, actual = last.DeviceId, elapsedMilliseconds = elapsed.ElapsedMilliseconds, result.Code,
            motionIssued = false, reconnected = false,
        }).ConfigureAwait(false);
        return result;
    }

    private bool IsBoundMountParked()
    {
        var mount = telescopeMediator.GetInfo();
        return mount.Connected && EnvironmentAdapterIdentityMatches(mount.DeviceId, configuration.ExpectedTelescopeId) &&
            mount.AtPark && !mount.Slewing && !mount.IsPulseGuiding && !mount.TrackingEnabled;
    }

    private async Task<GateResult> CloseBoundRoofOnceAsync(CancellationToken token, bool observeOnly = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(RoofTransitionLimit);
        try
        {
            return await roofCloseBoundary.CloseOnceAsync(domeMediator.GetInfo, CurrentRoofBoundaryGate, BoundRoofId,
                // Read back the required clearance again immediately at dispatch.
                ct => (configuration.Environment.RoofNeverInterferesWithMount ? IndependentRoofMountStopped() : IsBoundMountParked())
                    ? domeMediator.CloseShutter(ct) : Task.FromResult(false),
                ct => WaitForDomeOrRoofStateAsync(ShutterState.ShutterClosed, ct), timeout.Token, observeOnly).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return GateResult.Unknown("ROOF_TRANSITION_TIMEOUT", "关顶未在原过渡时限内确认；未重发命令，请现场核对。");
        }
    }
}
