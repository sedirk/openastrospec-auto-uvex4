using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3SettledScienceContinuationPolicyTests
{
    [Theory]
    [InlineData(ObservationStage.SelectAtrExposure, true)]
    [InlineData(ObservationStage.RunScienceBlock, true)]
    [InlineData(ObservationStage.FinalizeObservation, true)]
    [InlineData(ObservationStage.ValidateNightSetup, false)]
    [InlineData(ObservationStage.SlewToCatalogTarget, false)]
    [InlineData(ObservationStage.AcquireQhyWideField, false)]
    [InlineData(ObservationStage.CoarseCenter, false)]
    [InlineData(ObservationStage.AcquireG3SlitField, false)]
    [InlineData(ObservationStage.PlaceTargetOnSlit, false)]
    [InlineData(ObservationStage.StartGuiding, false)]
    [InlineData(ObservationStage.StartQhyPhotometry, false)]
    public void LongExposuresDoNotReopenOrResetAcquisitionAuthority(ObservationStage stage, bool allowed) =>
        Assert.Equal(allowed, G3SettledScienceContinuationPolicy.CanRetainExpiredAccounting(stage, "run", "run"));

    [Theory]
    [InlineData("old", "new")]
    [InlineData("", "")]
    public void OtherOrMissingRunCannotReuseExpiredAuthority(string ledger, string current) =>
        Assert.False(G3SettledScienceContinuationPolicy.CanRetainExpiredAccounting(ObservationStage.RunScienceBlock, ledger, current));
}
