namespace UvexAdv.Qhy.Core;

public sealed record QhyNinaWorkerIdentity(
    Guid ProfileId, Guid MasterProfileId, Guid SessionId, int ProcessId,
    DateTimeOffset ProcessStartedUtc, string ConfigurationSha256,
    string CameraId, string FilterWheelId, string FocuserId);
