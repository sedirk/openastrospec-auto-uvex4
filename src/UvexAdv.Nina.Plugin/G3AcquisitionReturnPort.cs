using NINA.Astrometry;
using UvexAdv.Observatory;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

// Only the original run adapter supplies these operations. The return workflow
// consumes the existing durable lineage; it cannot create/reset a movement budget,
// acquire a camera, reconnect a device, perform a lock shift or close the roof.
internal interface IG3AcquisitionReturnPort
{
    Phd2StateSnapshot GuideSnapshot { get; }
    Coordinates GetCurrentPosition();
    GateResult ValidateG3SearchMountState(string pierSide);
    GateResult ValidateG3DestinationPierSide(Coordinates destination, string pierSide);
    GateResult ValidateCommandCoordinateHorizon(ObservationContext context, Coordinates commanded, string action);
    Task RequireImmediatePhysicalActionGatesAsync(ObservationContext context, CancellationToken token);
    Task PersistG3AcquisitionMotionAsync(G3AcquisitionMotionState state, CancellationToken token);
    Task<bool> SlewToCoordinatesAsync(Coordinates destination, CancellationToken token);
    Task WaitForSlew(CancellationToken token);
    Task<G3PostSlewStabilityResult> WaitForG3PostSlewStabilityAsync(ObservationContext context,
        Coordinates commanded, Coordinates initialReported, string pierSide, string coordinateEpoch,
        string operation, double toleranceArcseconds, CancellationToken token);
    Task<string> PublishRunJsonEvidenceAsync(string kind, string caption, object payload,
        string? associatedFrame, CancellationToken token);
    void Report(string message, double value = -1);
}
