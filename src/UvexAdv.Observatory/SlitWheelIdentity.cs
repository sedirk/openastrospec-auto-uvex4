using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UvexAdv.Observatory;

/// <summary>
/// One empirically measured LED-illuminated slit-width fingerprint. The
/// nominal micrometre width is an operator-verified physical identity; it is
/// never inferred by scaling another slot's pixel width.
/// </summary>
public sealed record SlitWidthFingerprint(
    int WheelPosition,
    string SlitLabel,
    double NominalWidthMicrometers,
    double MeasuredWidthPixels,
    double WidthUncertaintyPixels,
    DateTimeOffset MeasuredUtc,
    string EvidenceSha256,
    SlitDarkApertureResolution Resolution = SlitDarkApertureResolution.Unresolved,
    double ReflectiveEdgeToApertureCenterPixels = double.NaN,
    double SecondaryEdgeAmplitudeRatio = double.NaN,
    string ShortExposureEvidenceSha256 = "",
    string LongExposureEvidenceSha256 = "");

/// <summary>
/// The declared UVEX4 wheel layout. This declaration is not optical proof;
/// commissioning must independently demonstrate that measured LED widths have
/// the same rank order before this mapping may be trusted.
/// </summary>
public static class UvexSlitWheelLayout
{
    private static readonly (int WheelPosition, double NominalWidthMicrometers)[] Slots =
    [
        (1, 300),
        (2, 15),
        (3, 25),
        (4, 35),
    ];

    public static IReadOnlyList<(int WheelPosition, double NominalWidthMicrometers)> DeclaredSlots { get; } =
        Array.AsReadOnly(Slots);
}

/// <summary>
/// Versioned optical identity library for one exact G3 detector geometry and
/// UVEX installation epoch. Four independently measured entries are required
/// so a mechanical position readback can be checked against optical reality.
/// </summary>
public sealed record SlitWheelIdentityCalibration(
    int SchemaVersion,
    string CalibrationId,
    string InstallationEpochId,
    string CameraStableId,
    int BinningX,
    int BinningY,
    int ImageWidthPixels,
    int ImageHeightPixels,
    double MaximumNormalizedResidual,
    double MinimumRunnerUpSeparationSigma,
    IReadOnlyList<SlitWidthFingerprint> Fingerprints,
    string CalibrationSha256,
    string MeasurementModelId = SlitDarkApertureHdrAnalyzer.MeasurementModelId,
    int ShortExposureMilliseconds = 10,
    int LongExposureMilliseconds = 20,
    double EdgePsfAlphaPixels = 0.625,
    double EdgePsfBeta = 0.43)
{
    public const int CurrentSchemaVersion = 2;
    public const int RequiredWheelPositionCount = 4;

    public SlitWheelIdentityCalibration WithComputedSha256() =>
        this with { CalibrationSha256 = ComputeContentSha256(this) };

    public bool HasValidContentSha256() =>
        IsSha256(CalibrationSha256) &&
        string.Equals(CalibrationSha256, ComputeContentSha256(this), StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
            issues.Add($"Slit-wheel identity schema must be {CurrentSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(CalibrationId)) issues.Add("Slit-wheel identity calibration ID is missing.");
        if (string.IsNullOrWhiteSpace(InstallationEpochId)) issues.Add("Slit-wheel installation epoch is missing.");
        if (string.IsNullOrWhiteSpace(CameraStableId)) issues.Add("Slit-wheel identity G3 camera identity is missing.");
        if (BinningX <= 0 || BinningY <= 0) issues.Add("Slit-wheel identity binning must be positive.");
        if (ImageWidthPixels <= 0 || ImageHeightPixels <= 0) issues.Add("Slit-wheel identity image dimensions must be positive.");
        if (!double.IsFinite(MaximumNormalizedResidual) || MaximumNormalizedResidual <= 0)
            issues.Add("Slit-wheel identity maximum normalized residual must be finite and positive.");
        if (!double.IsFinite(MinimumRunnerUpSeparationSigma) || MinimumRunnerUpSeparationSigma <= 0)
            issues.Add("Slit-wheel identity runner-up separation must be finite and positive.");
        if (!string.Equals(MeasurementModelId, SlitDarkApertureHdrAnalyzer.MeasurementModelId, StringComparison.Ordinal))
            issues.Add($"Slit-wheel identity measurement model must be {SlitDarkApertureHdrAnalyzer.MeasurementModelId}; bright-ridge FWHM evidence is obsolete.");
        if (ShortExposureMilliseconds <= 0 || LongExposureMilliseconds <= ShortExposureMilliseconds)
            issues.Add("Slit-wheel HDR exposures must be positive and the long exposure must exceed the short exposure.");
        if (!PositiveFinite(EdgePsfAlphaPixels) || !PositiveFinite(EdgePsfBeta))
            issues.Add("Slit-wheel shared edge-PSF parameters must be finite and positive.");

        if (Fingerprints is null || Fingerprints.Count != RequiredWheelPositionCount)
        {
            issues.Add($"Slit-wheel identity requires exactly {RequiredWheelPositionCount} independently measured physical slots.");
        }
        else
        {
            var positions = new HashSet<int>();
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nominalWidths = new HashSet<double>();
            var evidenceHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fingerprint in Fingerprints)
            {
                if (fingerprint.WheelPosition is < 1 or > RequiredWheelPositionCount || !positions.Add(fingerprint.WheelPosition))
                    issues.Add("Slit-wheel positions must be unique integers 1 through 4.");
                if (string.IsNullOrWhiteSpace(fingerprint.SlitLabel) || !labels.Add(fingerprint.SlitLabel))
                    issues.Add("Slit labels must be non-empty and unique.");
                if (!double.IsFinite(fingerprint.NominalWidthMicrometers) || fingerprint.NominalWidthMicrometers <= 0 ||
                    !nominalWidths.Add(fingerprint.NominalWidthMicrometers))
                    issues.Add("Nominal physical slit widths must be finite, positive and unique.");
                if (!double.IsFinite(fingerprint.MeasuredWidthPixels) || fingerprint.MeasuredWidthPixels <= 0)
                    issues.Add($"Slit position {fingerprint.WheelPosition} has an invalid measured LED width.");
                else if (fingerprint.MeasuredWidthPixels >= Math.Min(ImageWidthPixels, ImageHeightPixels) / 2d)
                    issues.Add($"Slit position {fingerprint.WheelPosition} LED width is too large for the commissioned image geometry.");
                if (!double.IsFinite(fingerprint.WidthUncertaintyPixels) || fingerprint.WidthUncertaintyPixels <= 0)
                    issues.Add($"Slit position {fingerprint.WheelPosition} requires a positive empirical width uncertainty.");
                if (fingerprint.MeasuredUtc == default)
                    issues.Add($"Slit position {fingerprint.WheelPosition} measurement timestamp is missing.");
                if (!IsSha256(fingerprint.EvidenceSha256))
                    issues.Add($"Slit position {fingerprint.WheelPosition} evidence SHA-256 is invalid.");
                else if (!evidenceHashes.Add(fingerprint.EvidenceSha256))
                    issues.Add("Every physical slit fingerprint requires distinct immutable LED evidence; evidence hashes cannot be reused across wheel positions.");
                if (fingerprint.Resolution == SlitDarkApertureResolution.Unresolved)
                    issues.Add($"Slit position {fingerprint.WheelPosition} physical dark aperture is unresolved; a reflected-edge width cannot be commissioned.");
                if (!double.IsFinite(fingerprint.ReflectiveEdgeToApertureCenterPixels))
                    issues.Add($"Slit position {fingerprint.WheelPosition} is missing its reflective-edge to physical-aperture-centre offset.");
                else if (PositiveFinite(fingerprint.MeasuredWidthPixels) && PositiveFinite(fingerprint.WidthUncertaintyPixels))
                {
                    // The LED reflective ridge is a blurred optical feature,
                    // not one of the two physical dark-aperture edges.  Its
                    // centroid may therefore sit several commissioned edge-
                    // PSF scales outside the fitted edge while still yielding
                    // a valid aperture midpoint.  Reject grossly inconsistent
                    // offsets, but do not force ridge-to-centre to equal half
                    // the physical slit width exactly.
                    var residual = Math.Abs(
                        Math.Abs(fingerprint.ReflectiveEdgeToApertureCenterPixels) -
                        fingerprint.MeasuredWidthPixels / 2d);
                    var tolerance = Math.Max(
                        fingerprint.WidthUncertaintyPixels * 3,
                        EdgePsfAlphaPixels * 4);
                    if (residual > tolerance)
                        issues.Add($"Slit position {fingerprint.WheelPosition} edge-to-centre offset is inconsistent with its physical aperture width.");
                }
                if (!double.IsFinite(fingerprint.SecondaryEdgeAmplitudeRatio) || fingerprint.SecondaryEdgeAmplitudeRatio is <= 0 or >= 1)
                    issues.Add($"Slit position {fingerprint.WheelPosition} requires a finite secondary-edge amplitude ratio between zero and one.");
                if (!IsSha256(fingerprint.ShortExposureEvidenceSha256) || !IsSha256(fingerprint.LongExposureEvidenceSha256))
                    issues.Add($"Slit position {fingerprint.WheelPosition} requires independent immutable short- and long-exposure evidence hashes.");
                else if (string.Equals(fingerprint.ShortExposureEvidenceSha256, fingerprint.LongExposureEvidenceSha256, StringComparison.OrdinalIgnoreCase))
                    issues.Add($"Slit position {fingerprint.WheelPosition} short and long HDR evidence hashes must be distinct.");
            }

            if (positions.Count == RequiredWheelPositionCount && !Enumerable.Range(1, RequiredWheelPositionCount).All(positions.Contains))
                issues.Add("Slit-wheel identity must cover positions 1, 2, 3 and 4 exactly once.");

            for (var leftIndex = 0; leftIndex < Fingerprints.Count; leftIndex++)
            for (var rightIndex = leftIndex + 1; rightIndex < Fingerprints.Count; rightIndex++)
            {
                var left = Fingerprints[leftIndex];
                var right = Fingerprints[rightIndex];
                if (!PositiveFinite(left.WidthUncertaintyPixels) || !PositiveFinite(right.WidthUncertaintyPixels) ||
                    !PositiveFinite(left.MeasuredWidthPixels) || !PositiveFinite(right.MeasuredWidthPixels)) continue;
                var combined = Math.Sqrt(
                    left.WidthUncertaintyPixels * left.WidthUncertaintyPixels +
                    right.WidthUncertaintyPixels * right.WidthUncertaintyPixels);
                var separation = Math.Abs(left.MeasuredWidthPixels - right.MeasuredWidthPixels) / combined;
                if (separation < MinimumRunnerUpSeparationSigma)
                {
                    issues.Add(
                        $"Slit positions {left.WheelPosition} and {right.WheelPosition} have insufficient empirical LED-width separation " +
                        $"({separation:F2} sigma < {MinimumRunnerUpSeparationSigma:F2} sigma).");
                }
            }

            // Names and wheel ordinals are precisely what this calibration is
            // meant to distrust.  The projected width need not be linear in
            // micrometres, but a physically wider slit must not measure
            // narrower than a physically narrower slit on the same locked
            // detector geometry.  This rank constraint catches an initially
            // swapped wheel installation instead of permanently blessing the
            // operator-entered labels as a new "calibration".
            var byNominalWidth = Fingerprints
                .Where(item => PositiveFinite(item.NominalWidthMicrometers) && PositiveFinite(item.MeasuredWidthPixels))
                .OrderBy(item => item.NominalWidthMicrometers)
                .ToArray();
            for (var index = 1; index < byNominalWidth.Length; index++)
            {
                var narrower = byNominalWidth[index - 1];
                var wider = byNominalWidth[index];
                if (wider.MeasuredWidthPixels <= narrower.MeasuredWidthPixels)
                {
                    issues.Add(
                        $"Optical slit-width order contradicts the declared physical identities: " +
                        $"{narrower.NominalWidthMicrometers:F0} µm at wheel position {narrower.WheelPosition} measured " +
                        $"{narrower.MeasuredWidthPixels:F3}px, while wider {wider.NominalWidthMicrometers:F0} µm at " +
                        $"position {wider.WheelPosition} measured {wider.MeasuredWidthPixels:F3}px. Suspect swapped installation, labels or ordinals.");
                }
            }
        }

        if (!HasValidContentSha256()) issues.Add("Slit-wheel identity calibration content SHA-256 does not match its payload.");
        return issues.AsReadOnly();
    }

    public static string ComputeContentSha256(SlitWheelIdentityCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        var ordered = calibration.Fingerprints?
            .OrderBy(item => item.WheelPosition)
            .ToArray() ?? [];
        var payload = new HashPayload(
            calibration.SchemaVersion,
            calibration.CalibrationId,
            calibration.InstallationEpochId,
            calibration.CameraStableId,
            calibration.BinningX,
            calibration.BinningY,
            calibration.ImageWidthPixels,
            calibration.ImageHeightPixels,
            calibration.MaximumNormalizedResidual,
            calibration.MinimumRunnerUpSeparationSigma,
            ordered,
            calibration.MeasurementModelId,
            calibration.ShortExposureMilliseconds,
            calibration.LongExposureMilliseconds,
            calibration.EdgePsfAlphaPixels,
            calibration.EdgePsfBeta);
        var json = JsonSerializer.Serialize(payload);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private sealed record HashPayload(
        int SchemaVersion,
        string CalibrationId,
        string InstallationEpochId,
        string CameraStableId,
        int BinningX,
        int BinningY,
        int ImageWidthPixels,
        int ImageHeightPixels,
        double MaximumNormalizedResidual,
        double MinimumRunnerUpSeparationSigma,
        IReadOnlyList<SlitWidthFingerprint> Fingerprints,
        string MeasurementModelId,
        int ShortExposureMilliseconds,
        int LongExposureMilliseconds,
        double EdgePsfAlphaPixels,
        double EdgePsfBeta);

    private static bool PositiveFinite(double value) => double.IsFinite(value) && value > 0;
    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);
}

public sealed record SlitIdentityCandidate(
    int WheelPosition,
    string SlitLabel,
    double NominalWidthMicrometers,
    double ReferenceWidthPixels,
    double CombinedUncertaintyPixels,
    double NormalizedResidual);

public sealed record SlitWheelIdentityResult(
    GateResult Gate,
    string CalibrationId,
    string CalibrationSha256,
    int ReportedWheelPosition,
    double ReportedNominalWidthMicrometers,
    double MeasuredWidthPixels,
    double MeasurementUncertaintyPixels,
    SlitIdentityCandidate? MatchedCandidate,
    IReadOnlyList<SlitIdentityCandidate> Candidates);

public static class SlitWheelIdentityMatcher
{
    public static SlitWheelIdentityResult Match(
        SlitWheelIdentityCalibration calibration,
        SlitDarkApertureHdrAnalysis measurement,
        int reportedWheelPosition,
        double reportedNominalWidthMicrometers,
        string cameraStableId,
        int binningX,
        int binningY,
        int imageWidthPixels,
        int imageHeightPixels)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(measurement);

        var measuredWidth = measurement.ApertureWidthPixels;
        var measuredUncertainty = measurement.WidthUncertaintyPixels;
        SlitWheelIdentityResult Result(GateResult gate, SlitIdentityCandidate? matched = null, IReadOnlyList<SlitIdentityCandidate>? candidates = null) =>
            new(
                gate,
                calibration.CalibrationId,
                calibration.CalibrationSha256,
                reportedWheelPosition,
                reportedNominalWidthMicrometers,
                measuredWidth,
                measuredUncertainty,
                matched,
                candidates ?? Array.Empty<SlitIdentityCandidate>());

        if (measurement.Gate.Disposition != GateDisposition.Passed)
        {
            return Result(GateResult.Unknown(
                "SLIT_LED_IDENTITY_GEOMETRY_UNAVAILABLE",
                $"HDR dark-aperture gate {measurement.Gate.Code} did not pass, so optical slit identity cannot be determined."));
        }

        var calibrationIssues = calibration.Validate();
        if (calibrationIssues.Count > 0)
        {
            return Result(GateResult.Unknown(
                "SLIT_LED_IDENTITY_CALIBRATION_INVALID",
                string.Join(" ", calibrationIssues)));
        }

        if (!string.Equals(calibration.CameraStableId, cameraStableId, StringComparison.OrdinalIgnoreCase) ||
            calibration.BinningX != binningX || calibration.BinningY != binningY ||
            calibration.ImageWidthPixels != imageWidthPixels || calibration.ImageHeightPixels != imageHeightPixels)
        {
            return Result(GateResult.Unknown(
                "SLIT_LED_IDENTITY_DETECTOR_MISMATCH",
                "The fresh LED frame camera/binning/image geometry does not match the commissioned slit-width fingerprint library."));
        }

        if (!double.IsFinite(measuredWidth) || measuredWidth <= 0 ||
            !double.IsFinite(measuredUncertainty) || measuredUncertainty <= 0)
        {
            return Result(GateResult.Unknown(
                "SLIT_LED_IDENTITY_MEASUREMENT_INVALID",
                "The fresh LED slit width or its uncertainty is not finite and positive."));
        }

        if (measurement.Resolution == SlitDarkApertureResolution.Unresolved)
        {
            return Result(GateResult.Unknown(
                "SLIT_LED_IDENTITY_DARK_APERTURE_UNRESOLVED",
                "The fresh HDR sequence located only a reflection; it did not resolve the physical dark aperture."));
        }

        var candidates = calibration.Fingerprints
            .Select(fingerprint =>
            {
                var combinedUncertainty = Math.Sqrt(
                    measuredUncertainty * measuredUncertainty +
                    fingerprint.WidthUncertaintyPixels * fingerprint.WidthUncertaintyPixels);
                return new SlitIdentityCandidate(
                    fingerprint.WheelPosition,
                    fingerprint.SlitLabel,
                    fingerprint.NominalWidthMicrometers,
                    fingerprint.MeasuredWidthPixels,
                    combinedUncertainty,
                    Math.Abs(measuredWidth - fingerprint.MeasuredWidthPixels) / combinedUncertainty);
            })
            .OrderBy(candidate => candidate.NormalizedResidual)
            .ThenBy(candidate => candidate.WheelPosition)
            .ToArray();
        var best = candidates[0];
        var runnerUp = candidates[1];
        var metrics = new Dictionary<string, double>
        {
            ["reportedWheelPosition"] = reportedWheelPosition,
            ["reportedNominalWidthMicrometers"] = reportedNominalWidthMicrometers,
            ["measuredWidthPixels"] = measuredWidth,
            ["measurementUncertaintyPixels"] = measuredUncertainty,
            ["matchedWheelPosition"] = best.WheelPosition,
            ["matchedNominalWidthMicrometers"] = best.NominalWidthMicrometers,
            ["bestNormalizedResidual"] = best.NormalizedResidual,
            ["runnerUpNormalizedResidual"] = runnerUp.NormalizedResidual,
            ["runnerUpSeparationSigma"] = runnerUp.NormalizedResidual - best.NormalizedResidual,
        };

        if (best.NormalizedResidual > calibration.MaximumNormalizedResidual)
        {
            return Result(GateResult.Fail(
                "SLIT_LED_IDENTITY_OUT_OF_FAMILY",
                $"Fresh LED width {measuredWidth:F2}±{measuredUncertainty:F2}px matches no commissioned physical slit; best is {best.SlitLabel} " +
                $"at {best.NormalizedResidual:F2} sigma (limit {calibration.MaximumNormalizedResidual:F2}).",
                metrics), best, candidates);
        }

        var separation = runnerUp.NormalizedResidual - best.NormalizedResidual;
        if (separation < calibration.MinimumRunnerUpSeparationSigma)
        {
            return Result(GateResult.Unknown(
                "SLIT_LED_IDENTITY_AMBIGUOUS",
                $"Fresh LED width {measuredWidth:F2}±{measuredUncertainty:F2}px cannot uniquely distinguish {best.SlitLabel} from {runnerUp.SlitLabel}; " +
                $"runner-up separation is {separation:F2} sigma (minimum {calibration.MinimumRunnerUpSeparationSigma:F2}).",
                metrics), best, candidates);
        }

        var reported = calibration.Fingerprints.SingleOrDefault(item => item.WheelPosition == reportedWheelPosition);
        if (reported is null ||
            !double.IsFinite(reportedNominalWidthMicrometers) ||
            Math.Abs(reported.NominalWidthMicrometers - reportedNominalWidthMicrometers) > 1e-6)
        {
            return Result(GateResult.Fail(
                "SLIT_LED_IDENTITY_DECLARATION_INVALID",
                $"Reported slit position {reportedWheelPosition} / {reportedNominalWidthMicrometers:F1}µm does not match the commissioned wheel declaration.",
                metrics), best, candidates);
        }

        if (best.WheelPosition != reportedWheelPosition ||
            Math.Abs(best.NominalWidthMicrometers - reportedNominalWidthMicrometers) > 1e-6)
        {
            return Result(GateResult.Fail(
                "SLIT_LED_IDENTITY_POSITION_MISMATCH",
                $"UVEX reports wheel position {reportedWheelPosition} ({reportedNominalWidthMicrometers:F1}µm), but the fresh LED width optically matches " +
                $"position {best.WheelPosition} ({best.SlitLabel}, {best.NominalWidthMicrometers:F1}µm). Suspect a wheel installation, label or ordinal mapping error; no automatic remapping is permitted.",
                metrics), best, candidates);
        }

        return Result(GateResult.Pass(
            "SLIT_LED_IDENTITY_MATCHED",
            $"Fresh LED width {measuredWidth:F2}±{measuredUncertainty:F2}px uniquely confirms wheel position {best.WheelPosition} " +
            $"({best.SlitLabel}, {best.NominalWidthMicrometers:F1}µm).",
            metrics), best, candidates);
    }
}
