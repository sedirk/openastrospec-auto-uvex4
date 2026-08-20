using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UvexAdv.Observatory;

/// <summary>
/// Versioned policy for collecting a same-pointing QHY/G3 plate-solve pair.
/// This policy authorizes at most one QHY exposure and no mount motion.  A
/// resulting record is a calibration candidate, not motion authority.
/// </summary>
public sealed record QhyG3FastPairPolicy(
    int SchemaVersion,
    string PolicyId,
    bool Enabled,
    double QuickQhyExposureSeconds,
    TimeSpan MaximumCachedQhyAge,
    TimeSpan MaximumPairMidpointSeparation,
    TimeSpan MaximumPairWallClock,
    double MaximumMountSpanArcseconds,
    TimeSpan CandidateValidity,
    double MaximumCandidateUncertaintyArcseconds)
{
    public const int CurrentSchemaVersion = 1;

    public static QhyG3FastPairPolicy Disabled { get; } = new(
        CurrentSchemaVersion,
        "qhy-g3-fast-pair-disabled",
        false,
        0,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        0,
        TimeSpan.Zero,
        0);

    public static TimeSpan ValidationTimeSpanFromSeconds(double value) =>
        double.IsFinite(value) && value > 0 && value < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(value)
            : TimeSpan.Zero;

    public static TimeSpan ValidationTimeSpanFromHours(double value) =>
        double.IsFinite(value) && value > 0 && value < TimeSpan.MaxValue.TotalHours
            ? TimeSpan.FromHours(value)
            : TimeSpan.Zero;

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
            issues.Add($"QHY/G3 fast-pair schema {SchemaVersion} is unsupported; expected {CurrentSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(PolicyId)) issues.Add("QHY/G3 fast-pair policy id is required.");
        if (!Enabled) return issues.AsReadOnly();
        Positive(issues, QuickQhyExposureSeconds, nameof(QuickQhyExposureSeconds));
        Positive(issues, MaximumCachedQhyAge.TotalSeconds, nameof(MaximumCachedQhyAge));
        Positive(issues, MaximumPairMidpointSeparation.TotalSeconds, nameof(MaximumPairMidpointSeparation));
        Positive(issues, MaximumPairWallClock.TotalSeconds, nameof(MaximumPairWallClock));
        Positive(issues, MaximumMountSpanArcseconds, nameof(MaximumMountSpanArcseconds));
        Positive(issues, CandidateValidity.TotalSeconds, nameof(CandidateValidity));
        Positive(issues, MaximumCandidateUncertaintyArcseconds, nameof(MaximumCandidateUncertaintyArcseconds));
        if (MaximumPairWallClock < MaximumPairMidpointSeparation)
            issues.Add("QHY/G3 fast-pair wall-clock limit cannot be shorter than its midpoint-separation limit.");
        if (MaximumCachedQhyAge > MaximumPairWallClock)
            issues.Add("QHY/G3 cached-frame age cannot exceed the complete pair wall-clock limit.");
        return issues.AsReadOnly();
    }

    private static void Positive(ICollection<string> issues, double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) issues.Add($"{name} must be positive and finite.");
    }
}

public enum QhyG3SolvePairSource
{
    ReusedFreshQhySolve,
    ImmediateSingleQhyExposure,
}

public enum QhyToG3TransferLifecycle
{
    Candidate,
    Verified,
    Active,
    Retired,
}

/// <summary>Immutable solve and detector provenance for one side of a pair.</summary>
public sealed record QhyG3SolvedFrame(
    string Role,
    string CameraStableId,
    string FramePath,
    string FrameSha256,
    string SolveEvidencePath,
    string SolveEvidenceSha256,
    DateTimeOffset ExposureStartedUtc,
    DateTimeOffset ExposureMidpointUtc,
    DateTimeOffset ExposureEndedUtc,
    DateTimeOffset SolveCompletedUtc,
    string TimingAuthority,
    int FrameWidthPixels,
    int FrameHeightPixels,
    int BinningX,
    int BinningY,
    int RoiOriginX,
    int RoiOriginY,
    int RoiWidthPixels,
    int RoiHeightPixels,
    double CenterRightAscensionDegrees,
    double CenterDeclinationDegrees,
    double PixelScaleArcseconds,
    double PositionAngleDegrees,
    bool Flipped,
    string MountBindingSha256);

/// <summary>A mount readback participating in the no-motion pair bracket.</summary>
public sealed record QhyG3PairMountReadback(
    string Role,
    double RightAscensionDegrees,
    double DeclinationDegrees,
    string CoordinateEpoch,
    string PierSide,
    DateTimeOffset ReportedUtc);

/// <summary>
/// Local tangent-plane relation measured by one same-pointing solve pair.  The
/// pre-position correction for a QHY-centred target is the negative of
/// G3MinusQhyEast/North.  Relative detector scale/rotation/parity are retained
/// as diagnostics but never substitute for G3PixelToMount slit placement.
/// </summary>
public sealed record QhyToG3TangentModel(
    string ModelKind,
    string ProjectionId,
    double ReferenceRightAscensionDegrees,
    double ReferenceDeclinationDegrees,
    double G3MinusQhyEastArcseconds,
    double G3MinusQhyNorthArcseconds,
    double PredictedPrepositionEastArcseconds,
    double PredictedPrepositionNorthArcseconds,
    double PredictedPrepositionMagnitudeArcseconds,
    double QhyPixelScaleArcseconds,
    double G3PixelScaleArcseconds,
    double QhyPixelsPerG3Pixel,
    double G3MinusQhyPositionAngleDegrees,
    bool RelativeParityFlipped,
    double EastVarianceArcsecondsSquared,
    double NorthVarianceArcsecondsSquared,
    double EastNorthCovarianceArcsecondsSquared,
    double PredictionUncertaintyArcseconds);

/// <summary>
/// A versioned candidate created from a successful pair.  Candidate records are
/// deliberately incapable of authorizing motion until a separate commissioning
/// process verifies and activates a multi-sample transfer record.
/// </summary>
public sealed record QhyToG3TransferCandidate(
    int SchemaVersion,
    string CalibrationId,
    int Version,
    QhyToG3TransferLifecycle Lifecycle,
    string CreatorMethod,
    QhyG3FastPairPolicy CollectionPolicy,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? VerifiedUtc,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ValidUntilUtc,
    string? SupersedesCalibrationId,
    string ObservationRunId,
    string ActionConfigurationSha256,
    string CommissioningPresetSha256,
    string NightSetupId,
    string NightSetupSha256,
    string InstallationEpochId,
    string TelescopeDeviceId,
    string QhyCameraStableId,
    string G3CameraStableId,
    string QhyOpticalTrainId,
    string G3OpticalTrainId,
    string PierSide,
    QhyG3SolvePairSource PairSource,
    double PairMidpointSeparationSeconds,
    double PairWallClockSeconds,
    double MaximumObservedMountSpanArcseconds,
    int SampleCount,
    QhyG3SolvedFrame Qhy,
    QhyG3SolvedFrame G3,
    IReadOnlyList<QhyG3PairMountReadback> MountReadbacks,
    QhyToG3TangentModel Model,
    double MaximumUncertaintyAllowedArcseconds,
    double? MinimumHourAngleDegrees,
    double? MaximumHourAngleDegrees,
    double? MinimumDeclinationDegrees,
    double? MaximumDeclinationDegrees,
    double? MinimumAltitudeDegrees,
    double? MaximumAltitudeDegrees,
    double? MinimumAzimuthDegrees,
    double? MaximumAzimuthDegrees,
    double? MinimumTemperatureC,
    double? MaximumTemperatureC,
    bool MotionAuthority,
    string CandidateSha256)
{
    public const int CurrentSchemaVersion = 1;

    public string ComputeCandidateSha256()
    {
        var fields = new List<string>
        {
            SchemaVersion.ToString(CultureInfo.InvariantCulture), CalibrationId,
            Version.ToString(CultureInfo.InvariantCulture), Lifecycle.ToString(), CreatorMethod,
            CollectionPolicy.SchemaVersion.ToString(CultureInfo.InvariantCulture), CollectionPolicy.PolicyId,
            CollectionPolicy.Enabled.ToString(CultureInfo.InvariantCulture),
            CollectionPolicy.QuickQhyExposureSeconds.ToString("R", CultureInfo.InvariantCulture),
            CollectionPolicy.MaximumCachedQhyAge.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
            CollectionPolicy.MaximumPairMidpointSeparation.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
            CollectionPolicy.MaximumPairWallClock.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
            CollectionPolicy.MaximumMountSpanArcseconds.ToString("R", CultureInfo.InvariantCulture),
            CollectionPolicy.CandidateValidity.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
            CollectionPolicy.MaximumCandidateUncertaintyArcseconds.ToString("R", CultureInfo.InvariantCulture),
            CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            VerifiedUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            ValidFromUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ValidUntilUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            SupersedesCalibrationId ?? string.Empty, ObservationRunId,
            NormalizeHash(ActionConfigurationSha256), NormalizeHash(CommissioningPresetSha256),
            NightSetupId, NormalizeHash(NightSetupSha256), InstallationEpochId, TelescopeDeviceId,
            QhyCameraStableId, G3CameraStableId, QhyOpticalTrainId, G3OpticalTrainId, PierSide,
            PairSource.ToString(), PairMidpointSeparationSeconds.ToString("R", CultureInfo.InvariantCulture),
            PairWallClockSeconds.ToString("R", CultureInfo.InvariantCulture),
            MaximumObservedMountSpanArcseconds.ToString("R", CultureInfo.InvariantCulture),
            SampleCount.ToString(CultureInfo.InvariantCulture),
            MaximumUncertaintyAllowedArcseconds.ToString("R", CultureInfo.InvariantCulture),
            MotionAuthority.ToString(CultureInfo.InvariantCulture),
        };
        AddFrame(fields, Qhy);
        AddFrame(fields, G3);
        foreach (var readback in MountReadbacks.OrderBy(item => item.ReportedUtc).ThenBy(item => item.Role, StringComparer.Ordinal))
        {
            fields.Add(readback.Role);
            fields.Add(readback.RightAscensionDegrees.ToString("R", CultureInfo.InvariantCulture));
            fields.Add(readback.DeclinationDegrees.ToString("R", CultureInfo.InvariantCulture));
            fields.Add(readback.CoordinateEpoch);
            fields.Add(readback.PierSide);
            fields.Add(readback.ReportedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
        fields.Add(Model.ModelKind);
        fields.Add(Model.ProjectionId);
        foreach (var value in new[]
                 {
                     Model.ReferenceRightAscensionDegrees, Model.ReferenceDeclinationDegrees,
                     Model.G3MinusQhyEastArcseconds, Model.G3MinusQhyNorthArcseconds,
                     Model.PredictedPrepositionEastArcseconds, Model.PredictedPrepositionNorthArcseconds,
                     Model.PredictedPrepositionMagnitudeArcseconds, Model.QhyPixelScaleArcseconds,
                     Model.G3PixelScaleArcseconds, Model.QhyPixelsPerG3Pixel,
                     Model.G3MinusQhyPositionAngleDegrees, Model.EastVarianceArcsecondsSquared,
                     Model.NorthVarianceArcsecondsSquared, Model.EastNorthCovarianceArcsecondsSquared,
                     Model.PredictionUncertaintyArcseconds,
                 })
            fields.Add(value.ToString("R", CultureInfo.InvariantCulture));
        fields.Add(Model.RelativeParityFlipped.ToString(CultureInfo.InvariantCulture));
        AddNullable(fields, MinimumHourAngleDegrees);
        AddNullable(fields, MaximumHourAngleDegrees);
        AddNullable(fields, MinimumDeclinationDegrees);
        AddNullable(fields, MaximumDeclinationDegrees);
        AddNullable(fields, MinimumAltitudeDegrees);
        AddNullable(fields, MaximumAltitudeDegrees);
        AddNullable(fields, MinimumAzimuthDegrees);
        AddNullable(fields, MaximumAzimuthDegrees);
        AddNullable(fields, MinimumTemperatureC);
        AddNullable(fields, MaximumTemperatureC);
        var canonical = string.Concat(fields.Select(value => $"{value.Length}:{value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public IReadOnlyList<string> ValidateIntegrity()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion) issues.Add("QHY-to-G3 transfer candidate schema is unsupported.");
        if (Lifecycle != QhyToG3TransferLifecycle.Candidate) issues.Add("A solve-pair record must remain Candidate until separately verified.");
        if (MotionAuthority) issues.Add("A single solve-pair candidate cannot authorize mount motion.");
        if (SampleCount != 1) issues.Add("A solve-pair candidate must contain exactly one source sample.");
        if (!CollectionPolicy.Enabled || CollectionPolicy.Validate().Count > 0)
            issues.Add("Candidate collection policy is disabled or invalid.");
        if (Math.Abs(CollectionPolicy.MaximumCandidateUncertaintyArcseconds - MaximumUncertaintyAllowedArcseconds) > 1e-9)
            issues.Add("Candidate uncertainty ceiling is not the hash-bound collection-policy ceiling.");
        if (!IsSha(CandidateSha256) || !SameHash(CandidateSha256, ComputeCandidateSha256())) issues.Add("Candidate self-hash is invalid.");
        if (CreatedUtc == default || ValidFromUtc != CreatedUtc || ValidUntilUtc <= ValidFromUtc) issues.Add("Candidate validity timestamps are invalid.");
        if (!FiniteModel(Model)) issues.Add("Candidate tangent model contains invalid numeric values.");
        if (Model.PredictionUncertaintyArcseconds > MaximumUncertaintyAllowedArcseconds) issues.Add("Candidate uncertainty exceeds its configured collection ceiling.");
        if (!QhyG3SolvePairBuilder.HasCompleteMountBracket(MountReadbacks))
            issues.Add("Candidate does not contain the exact G3/QHY/final five-readback bracket.");
        if (!IsSha(Qhy.FrameSha256) || !IsSha(Qhy.SolveEvidenceSha256) || !IsSha(Qhy.MountBindingSha256) ||
            !IsSha(G3.FrameSha256) || !IsSha(G3.SolveEvidenceSha256) || !IsSha(G3.MountBindingSha256))
            issues.Add("Candidate source hashes are incomplete or invalid.");
        return issues.AsReadOnly();
    }

    private static void AddFrame(ICollection<string> fields, QhyG3SolvedFrame frame)
    {
        fields.Add(frame.Role); fields.Add(frame.CameraStableId); fields.Add(frame.FramePath);
        fields.Add(NormalizeHash(frame.FrameSha256)); fields.Add(frame.SolveEvidencePath);
        fields.Add(NormalizeHash(frame.SolveEvidenceSha256));
        fields.Add(frame.ExposureStartedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        fields.Add(frame.ExposureMidpointUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        fields.Add(frame.ExposureEndedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        fields.Add(frame.SolveCompletedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        fields.Add(frame.TimingAuthority);
        fields.Add(frame.FrameWidthPixels.ToString(CultureInfo.InvariantCulture));
        fields.Add(frame.FrameHeightPixels.ToString(CultureInfo.InvariantCulture));
        fields.Add(frame.BinningX.ToString(CultureInfo.InvariantCulture));
        fields.Add(frame.BinningY.ToString(CultureInfo.InvariantCulture));
        fields.Add(frame.RoiOriginX.ToString(CultureInfo.InvariantCulture));
        fields.Add(frame.RoiOriginY.ToString(CultureInfo.InvariantCulture));
        fields.Add(frame.RoiWidthPixels.ToString(CultureInfo.InvariantCulture));
        fields.Add(frame.RoiHeightPixels.ToString(CultureInfo.InvariantCulture));
        fields.Add(frame.CenterRightAscensionDegrees.ToString("R", CultureInfo.InvariantCulture));
        fields.Add(frame.CenterDeclinationDegrees.ToString("R", CultureInfo.InvariantCulture));
        fields.Add(frame.PixelScaleArcseconds.ToString("R", CultureInfo.InvariantCulture));
        fields.Add(frame.PositionAngleDegrees.ToString("R", CultureInfo.InvariantCulture));
        fields.Add(frame.Flipped.ToString(CultureInfo.InvariantCulture));
        fields.Add(NormalizeHash(frame.MountBindingSha256));
    }

    private static void AddNullable(ICollection<string> fields, double? value) =>
        fields.Add(value?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty);

    private static bool FiniteModel(QhyToG3TangentModel model) =>
        new[]
        {
            model.ReferenceRightAscensionDegrees, model.ReferenceDeclinationDegrees,
            model.G3MinusQhyEastArcseconds, model.G3MinusQhyNorthArcseconds,
            model.PredictedPrepositionEastArcseconds, model.PredictedPrepositionNorthArcseconds,
            model.PredictedPrepositionMagnitudeArcseconds, model.QhyPixelScaleArcseconds,
            model.G3PixelScaleArcseconds, model.QhyPixelsPerG3Pixel,
            model.G3MinusQhyPositionAngleDegrees, model.EastVarianceArcsecondsSquared,
            model.NorthVarianceArcsecondsSquared, model.EastNorthCovarianceArcsecondsSquared,
            model.PredictionUncertaintyArcseconds,
        }.All(double.IsFinite);

    private static bool IsSha(string? value) => NormalizeHash(value).Length == 64 && NormalizeHash(value).All(Uri.IsHexDigit);
    private static bool SameHash(string? left, string? right) => string.Equals(NormalizeHash(left), NormalizeHash(right), StringComparison.OrdinalIgnoreCase);
    private static string NormalizeHash(string? value) => (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
}

public sealed record QhyG3SolvePairBuildRequest(
    QhyG3FastPairPolicy Policy,
    string ObservationRunId,
    string ActionConfigurationSha256,
    string CommissioningPresetSha256,
    string NightSetupId,
    string NightSetupSha256,
    string InstallationEpochId,
    string TelescopeDeviceId,
    string QhyOpticalTrainId,
    string G3OpticalTrainId,
    QhyG3SolvePairSource PairSource,
    QhyG3SolvedFrame Qhy,
    QhyG3SolvedFrame G3,
    IReadOnlyList<QhyG3PairMountReadback> MountReadbacks,
    DateTimeOffset CreatedUtc);

public sealed record QhyG3SolvePairBuildResult(GateResult Gate, QhyToG3TransferCandidate? Candidate);

public static class QhyG3SolvePairBuilder
{
    private static readonly string[] RequiredMountReadbackRoles =
    [
        "g3-before-exposure",
        "g3-after-exposure",
        "qhy-before-job",
        "qhy-after-accepted-frame",
        "pair-final-readback",
    ];

    internal static bool HasCompleteMountBracket(IReadOnlyList<QhyG3PairMountReadback> readbacks) =>
        readbacks.Count == RequiredMountReadbackRoles.Length &&
        RequiredMountReadbackRoles.All(required => readbacks.Count(item =>
            string.Equals(item.Role, required, StringComparison.Ordinal)) == 1);

    public static QhyG3SolvePairBuildResult Build(QhyG3SolvePairBuildRequest request)
    {
        var policyIssues = request.Policy.Validate();
        if (policyIssues.Count > 0)
            return Rejected("QHY_G3_PAIR_POLICY_INVALID", string.Join(" ", policyIssues));
        if (!request.Policy.Enabled)
            return Rejected("QHY_G3_PAIR_DISABLED", "Fast QHY/G3 solve-pair collection is disabled in the locked action configuration.");
        if (!ContextValid(request))
            return Rejected("QHY_G3_PAIR_CONTEXT_INVALID", "The run, action, commissioning, Night Setup, installation epoch, telescope or optical-train binding is missing.");
        var qhyIssue = ValidateFrame(request.Qhy, "QHY");
        if (qhyIssue is not null) return Rejected("QHY_G3_PAIR_QHY_INVALID", qhyIssue);
        var g3Issue = ValidateFrame(request.G3, "G3");
        if (g3Issue is not null) return Rejected("QHY_G3_PAIR_G3_INVALID", g3Issue);
        if (!HasCompleteMountBracket(request.MountReadbacks))
            return Rejected("QHY_G3_PAIR_MOUNT_BRACKET_INCOMPLETE", "The pair needs exactly one G3 before/after, QHY before/after and final fresh mount readback.");
        var g3Before = Readback(request.MountReadbacks, "g3-before-exposure");
        var g3After = Readback(request.MountReadbacks, "g3-after-exposure");
        var qhyBefore = Readback(request.MountReadbacks, "qhy-before-job");
        var qhyAfter = Readback(request.MountReadbacks, "qhy-after-accepted-frame");
        var finalReadback = Readback(request.MountReadbacks, "pair-final-readback");
        if (g3Before.ReportedUtc > request.G3.ExposureStartedUtc ||
            g3After.ReportedUtc < request.G3.ExposureEndedUtc ||
            qhyBefore.ReportedUtc > request.Qhy.ExposureStartedUtc ||
            qhyAfter.ReportedUtc < request.Qhy.ExposureEndedUtc ||
            finalReadback.ReportedUtc < g3After.ReportedUtc ||
            finalReadback.ReportedUtc < qhyAfter.ReportedUtc ||
            finalReadback.ReportedUtc < request.G3.SolveCompletedUtc ||
            finalReadback.ReportedUtc < request.Qhy.SolveCompletedUtc ||
            request.CreatedUtc < finalReadback.ReportedUtc)
            return Rejected("QHY_G3_PAIR_MOUNT_BRACKET_TIME_INVALID", "The five-readback bracket does not enclose both exposures, both solves and candidate creation in order.");

        var epoch = request.MountReadbacks[0].CoordinateEpoch;
        var pier = request.MountReadbacks[0].PierSide;
        if (!KnownPier(pier) || string.IsNullOrWhiteSpace(epoch) || request.MountReadbacks.Any(readback =>
                !FiniteCoordinate(readback.RightAscensionDegrees, readback.DeclinationDegrees) ||
                readback.ReportedUtc == default ||
                !string.Equals(readback.CoordinateEpoch, epoch, StringComparison.Ordinal) ||
                !string.Equals(readback.PierSide, pier, StringComparison.OrdinalIgnoreCase)))
            return Rejected("QHY_G3_PAIR_MOUNT_TOPOLOGY_CHANGED", "The pair mount bracket contains an invalid coordinate, unknown/changed pier side or changed coordinate epoch.");

        var maximumSpan = 0d;
        for (var left = 0; left < request.MountReadbacks.Count; left++)
        for (var right = left + 1; right < request.MountReadbacks.Count; right++)
        {
            var a = request.MountReadbacks[left];
            var b = request.MountReadbacks[right];
            var span = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
                a.RightAscensionDegrees, a.DeclinationDegrees,
                b.RightAscensionDegrees, b.DeclinationDegrees);
            if (!double.IsFinite(span)) return Rejected("QHY_G3_PAIR_MOUNT_SPAN_INVALID", "A spherical separation in the pair mount bracket is invalid.");
            maximumSpan = Math.Max(maximumSpan, span);
        }
        if (maximumSpan > request.Policy.MaximumMountSpanArcseconds + 1e-9)
            return Rejected(
                "QHY_G3_PAIR_MOUNT_MOVED",
                $"Mount readbacks span {maximumSpan:F2} arcsec (limit {request.Policy.MaximumMountSpanArcseconds:F2}); the two WCS solutions are not a same-pointing pair.",
                new Dictionary<string, double> { ["maximumMountSpanArcseconds"] = maximumSpan });

        var midpointSeparation = Math.Abs((request.Qhy.ExposureMidpointUtc - request.G3.ExposureMidpointUtc).TotalSeconds);
        var pairStart = request.Qhy.ExposureStartedUtc < request.G3.ExposureStartedUtc ? request.Qhy.ExposureStartedUtc : request.G3.ExposureStartedUtc;
        if (request.CreatedUtc < request.Qhy.SolveCompletedUtc || request.CreatedUtc < request.G3.SolveCompletedUtc)
            return Rejected("QHY_G3_PAIR_TIME_ORDER_INVALID", "Candidate creation precedes one of its source solve-completion timestamps.");
        var wallClock = (request.CreatedUtc - pairStart).TotalSeconds;
        if (midpointSeparation > request.Policy.MaximumPairMidpointSeparation.TotalSeconds + 1e-9 ||
            wallClock > request.Policy.MaximumPairWallClock.TotalSeconds + 1e-9)
            return Rejected(
                "QHY_G3_PAIR_TIME_WINDOW_EXCEEDED",
                $"Pair midpoint separation {midpointSeparation:F2}s / wall clock {wallClock:F2}s exceeds {request.Policy.MaximumPairMidpointSeparation.TotalSeconds:F2}s / {request.Policy.MaximumPairWallClock.TotalSeconds:F2}s.",
                new Dictionary<string, double>
                {
                    ["pairMidpointSeparationSeconds"] = midpointSeparation,
                    ["pairWallClockSeconds"] = wallClock,
                });

        var (east, north) = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(
            request.Qhy.CenterRightAscensionDegrees,
            request.Qhy.CenterDeclinationDegrees,
            request.G3.CenterRightAscensionDegrees,
            request.G3.CenterDeclinationDegrees);
        if (!double.IsFinite(east) || !double.IsFinite(north))
            return Rejected("QHY_G3_PAIR_TANGENT_MODEL_INVALID", "The QHY-to-G3 centre offset cannot be represented in the versioned TAN projection.");
        var magnitude = Math.Sqrt(east * east + north * north);
        var baseUncertainty = Math.Sqrt(
            request.Qhy.PixelScaleArcseconds * request.Qhy.PixelScaleArcseconds +
            request.G3.PixelScaleArcseconds * request.G3.PixelScaleArcseconds +
            maximumSpan * maximumSpan);
        if (!double.IsFinite(baseUncertainty) || baseUncertainty > request.Policy.MaximumCandidateUncertaintyArcseconds)
            return Rejected(
                "QHY_G3_PAIR_UNCERTAINTY_EXCEEDED",
                $"Conservative one-pair uncertainty {baseUncertainty:F2} arcsec exceeds {request.Policy.MaximumCandidateUncertaintyArcseconds:F2} arcsec.",
                new Dictionary<string, double> { ["predictionUncertaintyArcseconds"] = baseUncertainty });

        var model = new QhyToG3TangentModel(
            "QHY_TO_G3_LOCAL_TRANSLATION_V1",
            G3AcquisitionMotionState.CurrentTangentProjectionId,
            request.Qhy.CenterRightAscensionDegrees,
            request.Qhy.CenterDeclinationDegrees,
            east,
            north,
            -east,
            -north,
            magnitude,
            request.Qhy.PixelScaleArcseconds,
            request.G3.PixelScaleArcseconds,
            request.G3.PixelScaleArcseconds / request.Qhy.PixelScaleArcseconds,
            NormalizeSignedDegrees(request.G3.PositionAngleDegrees - request.Qhy.PositionAngleDegrees),
            request.Qhy.Flipped != request.G3.Flipped,
            baseUncertainty * baseUncertainty,
            baseUncertainty * baseUncertainty,
            0,
            baseUncertainty);
        var idSeed = string.Join("|", request.ObservationRunId, request.Qhy.FrameSha256, request.G3.FrameSha256, request.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        var calibrationId = "qhy-g3-pair-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idSeed)))[..24].ToLowerInvariant();
        var provisional = new QhyToG3TransferCandidate(
            QhyToG3TransferCandidate.CurrentSchemaVersion,
            calibrationId,
            1,
            QhyToG3TransferLifecycle.Candidate,
            "automatic-same-pointing-paired-wcs",
            request.Policy,
            request.CreatedUtc,
            null,
            request.CreatedUtc,
            request.CreatedUtc + request.Policy.CandidateValidity,
            null,
            request.ObservationRunId,
            request.ActionConfigurationSha256,
            request.CommissioningPresetSha256,
            request.NightSetupId,
            request.NightSetupSha256,
            request.InstallationEpochId,
            request.TelescopeDeviceId,
            request.Qhy.CameraStableId,
            request.G3.CameraStableId,
            request.QhyOpticalTrainId,
            request.G3OpticalTrainId,
            pier,
            request.PairSource,
            midpointSeparation,
            wallClock,
            maximumSpan,
            1,
            request.Qhy,
            request.G3,
            request.MountReadbacks.ToArray(),
            model,
            request.Policy.MaximumCandidateUncertaintyArcseconds,
            null, null,
            request.G3.CenterDeclinationDegrees, request.G3.CenterDeclinationDegrees,
            null, null, null, null, null, null,
            false,
            string.Empty);
        var candidate = provisional with { CandidateSha256 = provisional.ComputeCandidateSha256() };
        var integrity = candidate.ValidateIntegrity();
        if (integrity.Count > 0) return Rejected("QHY_G3_PAIR_CANDIDATE_INVALID", string.Join(" ", integrity));
        return new QhyG3SolvePairBuildResult(
            GateResult.Pass(
                "QHY_G3_PAIR_CANDIDATE_CREATED",
                $"Same-pointing WCS pair measured G3-QHY = ({east:+0.00;-0.00;0.00}, {north:+0.00;-0.00;0.00}) arcsec with {baseUncertainty:F2} arcsec conservative uncertainty; candidate cannot yet authorize motion.",
                new Dictionary<string, double>
                {
                    ["g3MinusQhyEastArcseconds"] = east,
                    ["g3MinusQhyNorthArcseconds"] = north,
                    ["prepositionMagnitudeArcseconds"] = magnitude,
                    ["pairMidpointSeparationSeconds"] = midpointSeparation,
                    ["pairWallClockSeconds"] = wallClock,
                    ["maximumMountSpanArcseconds"] = maximumSpan,
                    ["predictionUncertaintyArcseconds"] = baseUncertainty,
                }),
            candidate);
    }

    private static bool ContextValid(QhyG3SolvePairBuildRequest request) =>
        !string.IsNullOrWhiteSpace(request.ObservationRunId) && IsSha(request.ActionConfigurationSha256) &&
        IsSha(request.CommissioningPresetSha256) && !string.IsNullOrWhiteSpace(request.NightSetupId) &&
        IsSha(request.NightSetupSha256) && !string.IsNullOrWhiteSpace(request.InstallationEpochId) &&
        !string.IsNullOrWhiteSpace(request.TelescopeDeviceId) && !string.IsNullOrWhiteSpace(request.QhyOpticalTrainId) &&
        !string.IsNullOrWhiteSpace(request.G3OpticalTrainId) && request.CreatedUtc != default;

    private static QhyG3PairMountReadback Readback(
        IReadOnlyList<QhyG3PairMountReadback> readbacks,
        string role) => readbacks.Single(item => string.Equals(item.Role, role, StringComparison.Ordinal));

    private static string? ValidateFrame(QhyG3SolvedFrame frame, string label)
    {
        if (string.IsNullOrWhiteSpace(frame.Role) || string.IsNullOrWhiteSpace(frame.CameraStableId) ||
            string.IsNullOrWhiteSpace(frame.FramePath) || string.IsNullOrWhiteSpace(frame.SolveEvidencePath) ||
            !IsSha(frame.FrameSha256) || !IsSha(frame.SolveEvidenceSha256) || !IsSha(frame.MountBindingSha256))
            return $"{label} frame/solve identity or hashes are incomplete.";
        if (frame.ExposureStartedUtc == default || frame.ExposureMidpointUtc < frame.ExposureStartedUtc ||
            frame.ExposureEndedUtc < frame.ExposureMidpointUtc || frame.SolveCompletedUtc < frame.ExposureEndedUtc ||
            string.IsNullOrWhiteSpace(frame.TimingAuthority))
            return $"{label} exposure timestamps are invalid.";
        if (frame.FrameWidthPixels <= 0 || frame.FrameHeightPixels <= 0 ||
            frame.BinningX <= 0 || frame.BinningY <= 0 ||
            frame.RoiOriginX < 0 || frame.RoiOriginY < 0 ||
            frame.RoiWidthPixels != frame.FrameWidthPixels || frame.RoiHeightPixels != frame.FrameHeightPixels)
            return $"{label} ROI/binning geometry is invalid or incomplete.";
        if (!FiniteCoordinate(frame.CenterRightAscensionDegrees, frame.CenterDeclinationDegrees) ||
            !double.IsFinite(frame.PixelScaleArcseconds) || frame.PixelScaleArcseconds <= 0 ||
            !double.IsFinite(frame.PositionAngleDegrees))
            return $"{label} WCS is invalid.";
        return null;
    }

    private static QhyG3SolvePairBuildResult Rejected(string code, string message, IReadOnlyDictionary<string, double>? metrics = null) =>
        new(GateResult.Unknown(code, message, metrics), null);

    private static double NormalizeSignedDegrees(double value)
    {
        var normalized = ((value + 180) % 360 + 360) % 360 - 180;
        return normalized == -180 ? 180 : normalized;
    }

    private static bool FiniteCoordinate(double ra, double dec) =>
        double.IsFinite(ra) && ra is >= 0 and < 360 && double.IsFinite(dec) && dec is >= -90 and <= 90;

    private static bool KnownPier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "normal", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha(string? value)
    {
        var normalized = (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
    }
}
