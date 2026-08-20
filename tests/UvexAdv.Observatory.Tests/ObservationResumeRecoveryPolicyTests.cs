using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class ObservationResumeRecoveryPolicyTests
{
    [Theory]
    [InlineData(ObservationStage.ValidateNightSetup)]
    [InlineData(ObservationStage.SlewToCatalogTarget)]
    [InlineData(ObservationStage.AcquireQhyWideField)]
    [InlineData(ObservationStage.FinalizeObservation)]
    public void StagesOutsideSlitScienceWindowDoNotRestartPhysicalAcquisition(ObservationStage stage)
    {
        var plan = ObservationResumeRecoveryPolicy.ForStage(stage);

        Assert.False(plan.InvalidateG3AndGuideEpoch);
        Assert.False(plan.RequiresPreStageRecovery);
    }

    [Fact]
    public void CoarseCenterResumeDiscardsOldSolveAndReacquiresWideField()
    {
        var plan = ObservationResumeRecoveryPolicy.ForStage(ObservationStage.CoarseCenter);

        Assert.True(plan.InvalidateQhySolution);
        Assert.True(plan.ReacquireQhy);
        Assert.True(plan.RequiresPreStageRecovery);
    }

    [Fact]
    public void InterruptedG3StageInvalidatesEvidenceButLetsStageRecaptureItself()
    {
        var plan = ObservationResumeRecoveryPolicy.ForStage(ObservationStage.AcquireG3SlitField);

        Assert.True(plan.InvalidateG3AndGuideEpoch);
        Assert.False(plan.RequiresPreStageRecovery);
    }

    [Fact]
    public void SlitPlacementResumeReacquiresG3BeforeAnyCorrection()
    {
        var plan = ObservationResumeRecoveryPolicy.ForStage(ObservationStage.PlaceTargetOnSlit);

        Assert.True(plan.ReacquireG3);
        Assert.False(plan.ReplaceTargetOnSlit);
        Assert.False(plan.RestartGuiding);
        Assert.False(plan.RestorePhotometry);
    }

    [Fact]
    public void GuidingResumeReacquiresG3AndRevalidatesSlitFirst()
    {
        var plan = ObservationResumeRecoveryPolicy.ForStage(ObservationStage.StartGuiding);

        Assert.True(plan.ReacquireG3);
        Assert.True(plan.ReplaceTargetOnSlit);
        Assert.False(plan.RestartGuiding);
        Assert.False(plan.RestorePhotometry);
    }

    [Theory]
    [InlineData(ObservationStage.StartQhyPhotometry)]
    [InlineData(ObservationStage.SelectAtrExposure)]
    [InlineData(ObservationStage.RunScienceBlock)]
    public void ScienceSideResumeRebuildsEntireG3SlitGuidePhotometryChain(ObservationStage stage)
    {
        var plan = ObservationResumeRecoveryPolicy.ForStage(stage);

        Assert.True(plan.InvalidateG3AndGuideEpoch);
        Assert.True(plan.ReacquireG3);
        Assert.True(plan.ReplaceTargetOnSlit);
        Assert.True(plan.RestartGuiding);
        Assert.True(plan.RestorePhotometry);
        Assert.True(plan.RequiresPreStageRecovery);
    }
}
