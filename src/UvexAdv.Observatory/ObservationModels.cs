using System.Collections.ObjectModel;

namespace UvexAdv.Observatory;

public enum ObservationRunState
{
    Idle,
    Validating,
    RunningAuto,
    PauseRequested,
    Paused,
    PausedNeedsAttention,
    ManualTakeover,
    Cancelling,
    Finalizing,
    Completed,
    Cancelled,
    Faulted
}

public enum ObservationStage
{
    ValidateNightSetup,
    SlewToCatalogTarget,
    AcquireQhyWideField,
    CoarseCenter,
    AcquireG3SlitField,
    PlaceTargetOnSlit,
    StartGuiding,
    StartQhyPhotometry,
    SelectAtrExposure,
    RunScienceBlock,
    FinalizeObservation
}

public enum GateDisposition
{
    Passed,
    Failed,
    Indeterminate
}

/// <summary>
/// Describes operator impact independently from whether the state machine may
/// advance.  In particular, a Warning is an explicit, persisted downgrade: it
/// advances the run, while remaining visible in the dashboard and manifest.
/// </summary>
public enum GateSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record EquatorialTarget(
    string Name,
    string CatalogId,
    double RightAscensionDegrees,
    double DeclinationDegrees);

public sealed record ObservatorySite(
    double LatitudeDegrees,
    double LongitudeDegreesEast,
    double ElevationMeters);

public sealed record HorizonPoint(double AzimuthDegrees, double AltitudeDegrees);

public sealed record HorizonPolicy(
    double BaseMinimumAltitudeDegrees = 40,
    double StartMarginDegrees = 5,
    double ContinueMarginDegrees = 2,
    TimeSpan? SampleInterval = null,
    IReadOnlyList<HorizonPoint>? AzimuthProfile = null)
{
    public TimeSpan EffectiveSampleInterval => SampleInterval ?? TimeSpan.FromMinutes(2);
}

public sealed record MotionLimits(
    double MaximumSingleCorrectionDegrees = 0.5,
    double MaximumCumulativeCorrectionDegrees = 1.5,
    int MaximumCorrectionAttempts = 5,
    TimeSpan? MaximumAcquisitionTime = null)
{
    public TimeSpan EffectiveMaximumAcquisitionTime => MaximumAcquisitionTime ?? TimeSpan.FromMinutes(12);
}

/// <summary>
/// Declares what the G3 acquisition camera is expected to see at the catalogue
/// coordinate.  This is an observing-plan input, not a value inferred from a
/// target name.  Non-stellar modes keep catalogue/WCS geometry authoritative
/// when the science target is too faint, extended, or already hidden by the
/// slit to provide a repeatable stellar centroid.
/// </summary>
public enum TargetObservabilityClass
{
    DirectStellar,
    FaintPointSource,
    CompactExtended,
    ExtendedNebula,
    InvisibleInG3,
}

public sealed record ObservationPlan(
    string ObservationRunId,
    string NightSetupId,
    EquatorialTarget Target,
    ObservatorySite Site,
    DateTimeOffset PlannedStartUtc,
    TimeSpan PlannedDuration,
    HorizonPolicy Horizon,
    MotionLimits Motion,
    string ExpectedAtrCameraId,
    string ExpectedG3ProfileName,
    string ExpectedQhyCameraId,
    bool RequireSafetyMonitor = true,
    TargetObservabilityClass TargetObservability = TargetObservabilityClass.DirectStellar)
{
    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(ObservationRunId)) issues.Add("ObservationRunId is required.");
        if (string.IsNullOrWhiteSpace(NightSetupId)) issues.Add("NightSetupId is required.");
        if (string.IsNullOrWhiteSpace(Target.Name)) issues.Add("Target name is required.");
        if (!double.IsFinite(Target.RightAscensionDegrees) || Target.RightAscensionDegrees is < 0 or >= 360)
        {
            issues.Add("Right ascension must be finite and in [0, 360) degrees.");
        }
        if (!double.IsFinite(Target.DeclinationDegrees) || Target.DeclinationDegrees is < -90 or > 90)
        {
            issues.Add("Declination must be finite and in [-90, 90] degrees.");
        }
        if (Site.LatitudeDegrees is < -90 or > 90) issues.Add("Site latitude must be in [-90, 90] degrees.");
        if (Site.LongitudeDegreesEast is < -180 or > 180) issues.Add("Site longitude must be in [-180, 180] degrees east.");
        if (PlannedDuration <= TimeSpan.Zero) issues.Add("Planned duration must be positive.");
        if (Horizon.BaseMinimumAltitudeDegrees is < 0 or >= 90) issues.Add("Horizon altitude must be in [0, 90) degrees.");
        if (Horizon.StartMarginDegrees < 0 || Horizon.ContinueMarginDegrees < 0) issues.Add("Horizon margins cannot be negative.");
        if (Horizon.EffectiveSampleInterval <= TimeSpan.Zero) issues.Add("Horizon sample interval must be positive.");
        if (Motion.MaximumSingleCorrectionDegrees <= 0 || Motion.MaximumCumulativeCorrectionDegrees <= 0) issues.Add("Motion limits must be positive.");
        if (Motion.MaximumSingleCorrectionDegrees > Motion.MaximumCumulativeCorrectionDegrees) issues.Add("A single correction cannot exceed the cumulative correction limit.");
        if (Motion.MaximumCorrectionAttempts <= 0) issues.Add("Maximum correction attempts must be positive.");
        if (string.IsNullOrWhiteSpace(ExpectedAtrCameraId)) issues.Add("Expected ATR camera identity is required.");
        if (string.IsNullOrWhiteSpace(ExpectedG3ProfileName)) issues.Add("Expected PHD2/G3 profile is required.");
        if (string.IsNullOrWhiteSpace(ExpectedQhyCameraId)) issues.Add("Expected QHY camera identity is required.");
        return issues.AsReadOnly();
    }
}

public sealed record GateResult(
    string Code,
    GateDisposition Disposition,
    string Message,
    IReadOnlyDictionary<string, double>? Metrics = null,
    GateSeverity Severity = GateSeverity.Info)
{
    public static GateResult Pass(string code, string message, IReadOnlyDictionary<string, double>? metrics = null) =>
        new(code, GateDisposition.Passed, message, metrics, GateSeverity.Info);

    public static GateResult Warn(string code, string message, IReadOnlyDictionary<string, double>? metrics = null) =>
        new(code, GateDisposition.Passed, message, metrics, GateSeverity.Warning);

    public static GateResult Fail(string code, string message, IReadOnlyDictionary<string, double>? metrics = null) =>
        new(code, GateDisposition.Failed, message, metrics, GateSeverity.Error);

    public static GateResult Unknown(string code, string message, IReadOnlyDictionary<string, double>? metrics = null) =>
        new(code, GateDisposition.Indeterminate, message, metrics, GateSeverity.Error);
}

public sealed record StageResult(
    GateResult Gate,
    string? EvidencePath = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public bool CanAdvance => Gate.Disposition == GateDisposition.Passed;
}

public sealed record ObservationEvent(
    DateTimeOffset TimestampUtc,
    ObservationRunState State,
    ObservationStage? Stage,
    string Code,
    string Message,
    string? EvidencePath = null);

public sealed record ObservationSnapshot(
    string? ObservationRunId,
    ObservationRunState State,
    ObservationStage? CurrentStage,
    ObservationStage? NextStage,
    string StatusMessage,
    string? PauseReason,
    int CompletedStageCount,
    int TotalStageCount,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<ObservationEvent> RecentEvents)
{
    public static ObservationSnapshot Idle { get; } = new(
        null,
        ObservationRunState.Idle,
        null,
        null,
        "No observation is running.",
        null,
        0,
        ObservationRunCoordinator.Stages.Count,
        DateTimeOffset.UtcNow,
        Array.Empty<ObservationEvent>());
}

public sealed class ObservationContext
{
    private readonly object sync = new();
    private readonly Dictionary<string, object> values = new(StringComparer.OrdinalIgnoreCase);
    private Func<CancellationToken, Task>? checkpoint;
    private TimeSpan? remainingWorstCaseDuration;

    public ObservationContext(ObservationPlan plan) => Plan = plan;

    public ObservationPlan Plan { get; }

    public void Set<T>(string key, T value) where T : notnull
    {
        lock (sync) values[key] = value;
    }

    public bool TryGet<T>(string key, out T? value)
    {
        lock (sync)
        {
            if (values.TryGetValue(key, out var raw) && raw is T typed)
            {
                value = typed;
                return true;
            }
        }

        value = default;
        return false;
    }

    public IReadOnlyDictionary<string, object> Values
    {
        get
        {
            lock (sync) return new ReadOnlyDictionary<string, object>(new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// A runner sets this after choosing real exposure/cadence values. Checkpoints
    /// then protect the entire remaining bounded block, not merely the UI estimate.
    /// </summary>
    public TimeSpan? RemainingWorstCaseDuration
    {
        get { lock (sync) return remainingWorstCaseDuration; }
        set
        {
            if (value is { } duration && duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Remaining worst-case duration must be positive.");
            }
            lock (sync) remainingWorstCaseDuration = value;
        }
    }

    /// <summary>
    /// Must be awaited immediately before every new physical motion, exposure, or long-running sub-operation.
    /// It implements cooperative Pause/Resume/Takeover without aborting a frame that has already started.
    /// </summary>
    public Task CheckpointAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<CancellationToken, Task>? current;
        lock (sync) current = checkpoint;
        return current?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    internal void SetCheckpoint(Func<CancellationToken, Task> value)
    {
        lock (sync) checkpoint = value;
    }
}
