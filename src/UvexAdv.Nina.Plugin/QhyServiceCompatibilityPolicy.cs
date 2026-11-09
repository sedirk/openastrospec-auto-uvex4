using UvexAdv.Qhy.Core;

namespace UvexAdv.Nina.Plugin;

internal static class QhyServiceCompatibilityPolicy
{
    internal static bool HasOwnerIdleCapability(IReadOnlyList<string>? capabilities) =>
        capabilities?.Contains(QhyControlProtocol.OwnerIdleCapability, StringComparer.Ordinal) == true;
}
