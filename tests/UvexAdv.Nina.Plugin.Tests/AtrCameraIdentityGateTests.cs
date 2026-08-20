using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrCameraIdentityGateTests
{
    [Fact]
    public void ExactBoundDeviceIdTakesPrecedenceOverFriendlyName()
    {
        Assert.False(AtrCameraIdentityGate.Matches(
            "ATR585M|ATR585M|ToupTek ATR585M|actual-id",
            "actual-id",
            "ATR585M",
            "different-bound-id"));

        Assert.True(AtrCameraIdentityGate.Matches(
            "renamed camera|renamed camera||bound-id",
            "bound-id",
            "ATR585M",
            "bound-id"));
    }

    [Fact]
    public void ModelNameIsOnlyAFallbackBeforeStableBindingExists()
    {
        Assert.True(AtrCameraIdentityGate.Matches(
            "ATR585M|ATR585M||temporary-id",
            "temporary-id",
            "ATR585M",
            string.Empty));

        Assert.False(AtrCameraIdentityGate.Matches(
            "Different camera|Different camera||temporary-id",
            "temporary-id",
            "ATR585M",
            string.Empty));
    }
}
