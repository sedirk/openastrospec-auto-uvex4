using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// One immediate exposure may consume the fresh optical window just measured by
/// in-place recovery. Not a persistent cache and never transferable across runs.
/// </summary>
internal sealed class Phd2PreExposureReceipt(
    string runId, string frameSha256, DateTimeOffset frameCompletedUtc, Phd2StateSnapshot owner)
{
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(5);
    private bool consumed;

    internal bool TryConsume(string currentRunId, string currentFrameSha256,
        Phd2StateSnapshot current, DateTimeOffset now, bool guidingStable)
    {
        if (consumed) return false;
        consumed = true; // Even an invalid receipt cannot be saved for later.
        return guidingStable && currentRunId == runId && frameSha256.Length == 64 &&
            string.Equals(frameSha256, currentFrameSha256, StringComparison.OrdinalIgnoreCase) &&
            now >= frameCompletedUtc && now - frameCompletedUtc <= MaximumAge &&
            owner.GuideOutput?.Failed != true && current.GuideOutput?.Failed != true &&
            owner.LastConfigurationChange == current.LastConfigurationChange &&
            Phd2ReadOnlyFrameGraceBudget.SameGuiding(owner, current);
    }
}
