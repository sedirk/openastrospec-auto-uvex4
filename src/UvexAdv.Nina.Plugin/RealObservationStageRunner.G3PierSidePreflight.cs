using NINA.Astrometry;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed partial class RealObservationStageRunner
{
    private bool reportedDestinationSideUnavailable;

    private GateResult ValidateG3DestinationPierSide(Coordinates destination, string expectedSide)
    {
        // Call the existing sole owner's API. No secondary ASCOM connection,
        // hour-angle guess, side setter, flip command or direct serial access.
        var gate = G3DestinationPierSidePreflight.Check(expectedSide,
            () => telescopeMediator.DestinationSideOfPier(destination));
        if (gate.Code == "G3_DESTINATION_PIER_SIDE_UNAVAILABLE" && !reportedDestinationSideUnavailable)
        {
            reportedDestinationSideUnavailable = true;
            Report("赤道仪驱动未提供目的地侧别预测；未声称同侧已确认，仍执行原有动作前后侧别、限位和回程账本检查。");
        }
        return gate;
    }
}
