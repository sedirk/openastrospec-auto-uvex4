using UvexAdv.Nina.Plugin;

namespace UvexAdv.Nina.Plugin.UiHarness;

public sealed class NativeFailurePolicyMock
{
    public IReadOnlyList<SpectroscopyFailurePolicyChoice> AvailableFailurePolicies => SpectroscopyFailurePolicyChoice.Choices;
    public SpectroscopyTargetFailurePolicy FailurePolicy { get; set; }
}
