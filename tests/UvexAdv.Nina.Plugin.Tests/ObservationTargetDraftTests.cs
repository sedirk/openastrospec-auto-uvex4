using UvexAdv.Nina.Plugin;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class ObservationTargetDraftTests
{
    private static ObservationTargetDraft Valid => new("Deneb / 天津四", "HIP 102098", 310.35798, 45.28034);

    [Fact]
    public void VisibleAndBackendParametersUseTheSameApplyAction()
    {
        var applied = new List<ObservationTargetDraft>();
        var command = new ObservationTargetDraftCommand(() => Valid, applied.Add, () => true);
        command.Execute(null);
        command.Execute(Valid);
        Assert.Equal(new[] { Valid, Valid }, applied);
        Assert.Equal("J2000", applied[1].ToImportResult().Epoch);
    }

    [Fact]
    public void ActiveRunCannotChangeTargetThroughEitherEntry()
    {
        var count = 0;
        var command = new ObservationTargetDraftCommand(() => Valid, _ => count++, () => false);
        command.Execute(null);
        command.Execute(Valid);
        Assert.Equal(0, count);
        Assert.False(command.CanExecute(Valid));
    }

    [Theory]
    [InlineData(null, 45d)]
    [InlineData(310d, null)]
    [InlineData(360d, 45d)]
    [InlineData(-1d, 45d)]
    [InlineData(310d, 91d)]
    [InlineData(double.NaN, 45d)]
    public void MissingOrInvalidCoordinatesCannotSilentlyBecomeAnOrigin(double? ra, double? dec)
    {
        var draft = Valid with { RightAscensionDegrees = ra, DeclinationDegrees = dec };
        var command = new ObservationTargetDraftCommand(() => draft, _ => throw new Exception("Must not apply"), () => true);
        Assert.False(command.CanExecute(draft));
        command.Execute(draft);
        Assert.Throws<ArgumentException>(() => draft.ToImportResult());
    }
}
