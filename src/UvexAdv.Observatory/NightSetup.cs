namespace UvexAdv.Observatory;

public enum CalibrationStrategy
{
    BrightReferenceStar,
    CompactEmissionLineObject,
    ExternalCalibrationLamp,
    NightSkyLines,
    None
}

public enum DispersionDirection
{
    BlueAtLeftRedAtRight,
    RedAtLeftBlueAtRight
}

public sealed record CameraSetup(
    string StableDeviceId,
    int Gain,
    int Offset,
    int BinningX,
    int BinningY,
    double? TemperatureC,
    string ReadoutMode,
    int RoiX,
    int RoiY,
    int RoiWidth,
    int RoiHeight);

public sealed record NightSetupRecord(
    int SchemaVersion,
    string NightSetupId,
    DateTimeOffset LockedUtc,
    int SlitPosition,
    double SlitWidthMicrometers,
    int GratingPositionSteps,
    double NominalCentralWavelengthNanometers,
    int M2PositionSteps,
    int? TelescopeFocusPositionSteps,
    CameraSetup Atr585m,
    string G3StableDeviceId,
    string Phd2ProfileName,
    CameraSetup QhyMiniCam8m,
    DispersionDirection DispersionDirection,
    double ExpectedMinimumWavelengthNanometers,
    double ExpectedMaximumWavelengthNanometers,
    CalibrationStrategy CalibrationStrategy,
    string CalibrationReference,
    HorizonPolicy HorizonPolicy,
    string SafetyCapability,
    bool LongWavelengthOrderSortingFilterInstalled = false,
    double? SecondOrderRiskOnsetAngstrom = 6800,
    IReadOnlyList<FocusDomainBinding>? FocusDomains = null,
    int G3SaturationAdu = 4095)
{
    public const int LegacySchemaVersion = 1;
    public const int CurrentSchemaVersion = 2;

    public IReadOnlyList<string> Validate()
    {
        var issues = new List<string>();
        if (SchemaVersion is not (LegacySchemaVersion or CurrentSchemaVersion)) issues.Add($"Unsupported Night Setup schema version {SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(NightSetupId)) issues.Add("Night Setup ID is required.");
        if (SlitPosition is < 1 or > 4) issues.Add("Slit position must be 1 through 4.");
        var expectedWidth = SlitPosition switch { 1 => 300, 2 => 15, 3 => 25, 4 => 35, _ => double.NaN };
        if (!double.IsNaN(expectedWidth) && Math.Abs(SlitWidthMicrometers - expectedWidth) > 0.01)
        {
            issues.Add($"Slit position {SlitPosition} must be recorded as {expectedWidth:F0} µm for this UVEX4 wheel.");
        }
        if (ExpectedMinimumWavelengthNanometers >= ExpectedMaximumWavelengthNanometers) issues.Add("Expected wavelength limits are reversed or empty.");
        if (string.IsNullOrWhiteSpace(Atr585m.StableDeviceId)) issues.Add("ATR585M stable identity is required.");
        if (Atr585m.Gain != 100) issues.Add($"ATR585M gain is {Atr585m.Gain}; the commissioned preset requires gain 100.");
        if (Atr585m.Offset != 256) issues.Add($"ATR585M offset is {Atr585m.Offset}; the commissioned preset requires offset 256.");
        if (Atr585m.BinningX != Atr585m.BinningY) issues.Add("ATR585M asymmetric binning is not commissioned.");
        if (string.IsNullOrWhiteSpace(G3StableDeviceId)) issues.Add("G3M2210M stable identity is required.");
        if (string.IsNullOrWhiteSpace(Phd2ProfileName)) issues.Add("PHD2 profile name is required.");
        if (G3SaturationAdu is <= 0 or > ushort.MaxValue) issues.Add("G3 saturation ADU must be within the unsigned 16-bit FITS container range.");
        if (string.IsNullOrWhiteSpace(QhyMiniCam8m.StableDeviceId)) issues.Add("QHYminiCam8M stable identity is required.");
        if (CalibrationStrategy == CalibrationStrategy.None) issues.Add("No wavelength calibration strategy is selected.");
        if (CalibrationStrategy != CalibrationStrategy.None && string.IsNullOrWhiteSpace(CalibrationReference)) issues.Add("Calibration reference is required.");
        if (!LongWavelengthOrderSortingFilterInstalled && SecondOrderRiskOnsetAngstrom is null) issues.Add("Second-order risk must remain flagged when no order-sorting filter is installed.");
        if (SchemaVersion == CurrentSchemaVersion)
        {
            issues.AddRange(FocusDomainConventions.ValidateBindings(this));
            var main = FocusDomains?.FirstOrDefault(binding => binding.Role == FocusDomainRole.C11Main);
            if (TelescopeFocusPositionSteps is { } legacyPosition && main is not null && legacyPosition != main.StartPositionSteps)
            {
                issues.Add("Legacy TelescopeFocusPositionSteps, when retained, must mirror the C11Main start position; it never represents GS350 or UVEX M2 focus.");
            }
            var spectral = FocusDomains?.FirstOrDefault(binding => binding.Role == FocusDomainRole.UvexSpectral);
            if (spectral is not null && M2PositionSteps != spectral.StartPositionSteps)
            {
                issues.Add("M2PositionSteps must match the independent UvexSpectral focus-domain start position.");
            }
        }
        return issues.AsReadOnly();
    }
}

public sealed record LiveSetupState(
    string AtrCameraId,
    int AtrGain,
    int AtrOffset,
    int AtrBinningX,
    int AtrBinningY,
    double? AtrTemperatureC,
    string AtrReadoutMode,
    int SlitPosition,
    int GratingPositionSteps,
    int M2PositionSteps,
    string Phd2ProfileName,
    string G3CameraId,
    string QhyCameraId,
    IReadOnlyList<LiveFocusDomainState>? FocusDomains = null);

public static class NightSetupCompatibility
{
    public static IReadOnlyList<GateResult> Evaluate(
        NightSetupRecord setup,
        LiveSetupState live,
        double temperatureToleranceC = 0.5,
        DateTimeOffset? evaluatedUtc = null)
    {
        var gates = new List<GateResult>();
        AddIdentity(gates, "ATR_IDENTITY", setup.Atr585m.StableDeviceId, live.AtrCameraId);
        AddEquality(gates, "ATR_GAIN", setup.Atr585m.Gain, live.AtrGain, "ATR gain");
        AddEquality(gates, "ATR_OFFSET", setup.Atr585m.Offset, live.AtrOffset, "ATR offset");
        AddEquality(gates, "ATR_BIN_X", setup.Atr585m.BinningX, live.AtrBinningX, "ATR X binning");
        AddEquality(gates, "ATR_BIN_Y", setup.Atr585m.BinningY, live.AtrBinningY, "ATR Y binning");
        if (setup.Atr585m.TemperatureC is { } expectedTemperature && live.AtrTemperatureC is { } actualTemperature)
        {
            gates.Add(Math.Abs(expectedTemperature - actualTemperature) <= temperatureToleranceC
                ? GateResult.Pass("ATR_TEMPERATURE", $"ATR temperature {actualTemperature:F2} °C is within tolerance.")
                : GateResult.Fail("ATR_TEMPERATURE", $"ATR temperature {actualTemperature:F2} °C differs from {expectedTemperature:F2} °C by more than {temperatureToleranceC:F2} °C."));
        }
        else if (setup.Atr585m.TemperatureC is not null)
        {
            gates.Add(GateResult.Unknown("ATR_TEMPERATURE", "ATR temperature is unavailable."));
        }
        AddEquality(gates, "SLIT_POSITION", setup.SlitPosition, live.SlitPosition, "slit position");
        AddEquality(gates, "GRATING_POSITION", setup.GratingPositionSteps, live.GratingPositionSteps, "grating position");
        AddEquality(gates, "M2_POSITION", setup.M2PositionSteps, live.M2PositionSteps, "M2 position");
        AddIdentity(gates, "PHD2_PROFILE", setup.Phd2ProfileName, live.Phd2ProfileName);
        AddIdentity(gates, "G3_IDENTITY", setup.G3StableDeviceId, live.G3CameraId);
        AddIdentity(gates, "QHY_IDENTITY", setup.QhyMiniCam8m.StableDeviceId, live.QhyCameraId);
        AddFocusDomainGates(gates, setup, live.FocusDomains, evaluatedUtc ?? DateTimeOffset.UtcNow);
        return gates.AsReadOnly();
    }

    private static void AddFocusDomainGates(
        List<GateResult> gates,
        NightSetupRecord setup,
        IReadOnlyList<LiveFocusDomainState>? liveDomains,
        DateTimeOffset evaluatedUtc)
    {
        if (setup.SchemaVersion == NightSetupRecord.LegacySchemaVersion)
        {
            gates.Add(GateResult.Unknown(
                "FOCUS_DOMAINS_LEGACY_SCHEMA",
                "Night Setup schema 1 is readable, but its single TelescopeFocusPositionSteps value cannot attest the independent C11/Gemini, GS350/ToupTek AAF, and UVEX/M2 focus domains."));
            return;
        }
        if (setup.SchemaVersion != NightSetupRecord.CurrentSchemaVersion)
        {
            gates.Add(GateResult.Fail("FOCUS_DOMAINS_SCHEMA", $"Night Setup schema {setup.SchemaVersion} cannot establish focus-domain compatibility."));
            return;
        }
        if (setup.FocusDomains is null || setup.FocusDomains.Count != 3)
        {
            gates.Add(GateResult.Fail("FOCUS_DOMAINS_REQUIRED", "The locked Night Setup does not contain exactly three independent focus-domain bindings."));
            return;
        }

        foreach (var role in Enum.GetValues<FocusDomainRole>())
        {
            var code = "FOCUS_" + FocusDomainConventions.Code(role);
            var expectedMatches = setup.FocusDomains.Where(binding => binding.Role == role).ToArray();
            if (expectedMatches.Length != 1)
            {
                gates.Add(GateResult.Fail(code + "_BINDING", $"The locked Night Setup has {expectedMatches.Length} bindings for {role}; exactly one is required."));
                continue;
            }

            var expected = expectedMatches[0];
            gates.Add(evaluatedUtc >= expected.VerifiedUtc && evaluatedUtc <= expected.ValidUntilUtc
                ? GateResult.Pass(code + "_EVIDENCE_FRESHNESS", $"{role} focus evidence is valid through {expected.ValidUntilUtc:O}.")
                : GateResult.Fail(code + "_EVIDENCE_FRESHNESS", $"{role} focus evidence verified at {expected.VerifiedUtc:O} is not valid at {evaluatedUtc:O}; validity ended at {expected.ValidUntilUtc:O}."));
            gates.Add(double.IsFinite(expected.Confidence) && expected.Confidence > 0 && expected.Confidence <= 1
                ? GateResult.Pass(code + "_CONFIDENCE", $"{role} focus evidence confidence is {expected.Confidence:F3}.", new Dictionary<string, double> { ["confidence"] = expected.Confidence })
                : GateResult.Unknown(code + "_CONFIDENCE", $"{role} focus evidence confidence is missing or invalid."));

            if (liveDomains is null)
            {
                gates.Add(GateResult.Unknown(code + "_LIVE_STATE", $"No read-only live identity/position snapshot is available from the owner of {role}; another focus domain is not substituted."));
                continue;
            }
            var liveMatches = liveDomains.Where(candidate => candidate.Role == role).ToArray();
            if (liveMatches.Length == 0)
            {
                gates.Add(GateResult.Unknown(code + "_LIVE_STATE", $"The owner did not report a live {role} focus snapshot; another focus domain is not substituted."));
                continue;
            }
            if (liveMatches.Length > 1)
            {
                gates.Add(GateResult.Fail(code + "_LIVE_STATE", $"The owner reported {liveMatches.Length} live {role} focus snapshots, so identity is ambiguous."));
                continue;
            }

            var actual = liveMatches[0];
            var identityMatches = string.Equals(expected.Owner, actual.Owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(expected.LogicalDeviceId, actual.LogicalDeviceId, StringComparison.OrdinalIgnoreCase)
                && expected.PhysicalBinding is not null
                && actual.PhysicalBinding is not null
                && expected.PhysicalBinding.Mechanism == actual.PhysicalBinding.Mechanism
                && string.Equals(expected.PhysicalBinding.ConnectionEndpoint, actual.PhysicalBinding.ConnectionEndpoint, StringComparison.OrdinalIgnoreCase)
                && string.Equals(expected.PhysicalBinding.HardwareInstanceId, actual.PhysicalBinding.HardwareInstanceId, StringComparison.OrdinalIgnoreCase);
            gates.Add(identityMatches
                ? GateResult.Pass(code + "_IDENTITY", $"{role} owner, logical ID, mechanism, endpoint, and physical instance match the locked binding.")
                : GateResult.Fail(code + "_IDENTITY", $"{role} live owner/logical/physical identity does not match the locked binding; another mechanism cannot be substituted."));

            var topologyMatches = expected.PhysicalBinding is not null
                && actual.PhysicalBinding is not null
                && (string.IsNullOrWhiteSpace(expected.PhysicalBinding.TopologyPath)
                    || string.Equals(expected.PhysicalBinding.TopologyPath.Trim(), actual.PhysicalBinding.TopologyPath?.Trim(), StringComparison.OrdinalIgnoreCase));
            gates.Add(topologyMatches
                ? GateResult.Pass(code + "_TOPOLOGY", string.IsNullOrWhiteSpace(expected.PhysicalBinding?.TopologyPath)
                    ? $"{role} uses its exact hardware-instance binding; no additional topology lock is required for this role."
                    : $"{role} physical topology matches the locked binding.")
                : GateResult.Fail(code + "_TOPOLOGY", $"{role} physical topology changed from '{expected.PhysicalBinding?.TopologyPath}' to '{actual.PhysicalBinding?.TopologyPath}'."));

            if (actual.PositionSteps is not { } position)
            {
                if (role == FocusDomainRole.Gs350WideField &&
                    expected.Limits is { MaximumSingleMoveSteps: 0, MaximumCumulativeMoveSteps: 0 })
                {
                    var metricGate = EvaluateLiveMetric(code, setup, expected, actual.Metric, evaluatedUtc);
                    gates.Add(metricGate);
                    gates.Add(identityMatches && topologyMatches && metricGate.Disposition == GateDisposition.Passed
                        ? GateResult.Pass(code + "_POSITION", "GS350 automatic focus motion is explicitly disabled; no live position is claimed, and the current QHY frame independently passed the locked metric source/role and validity gates.")
                        : GateResult.Unknown(code + "_POSITION", "GS350 has no attestable live position. A manual lock can proceed only with matching identity/topology, zero automatic-motion limits, and a current passing QHY focus/plate-solve metric."));
                }
                else
                {
                    gates.Add(GateResult.Unknown(code + "_POSITION", $"{role} live position is unavailable; this domain requires an actual owner-reported position."));
                }
            }
            else if (expected.Limits is null || position < expected.Limits.MinimumPositionSteps || position > expected.Limits.MaximumPositionSteps)
            {
                gates.Add(GateResult.Fail(code + "_POSITION", $"{role} live position {position} is outside the locked allowed range."));
            }
            else
            {
                gates.Add(position == expected.StartPositionSteps
                    ? GateResult.Pass(code + "_POSITION", $"{role} live position matches the locked start position {position} steps.")
                    : GateResult.Fail(code + "_POSITION", $"{role} live position {position} does not match locked start position {expected.StartPositionSteps}."));
            }
        }
    }

    private static GateResult EvaluateLiveMetric(
        string code,
        NightSetupRecord setup,
        FocusDomainBinding expected,
        LiveFocusMetricState? liveMetric,
        DateTimeOffset evaluatedUtc)
    {
        if (liveMetric is null)
        {
            return GateResult.Unknown(code + "_LIVE_METRIC", "No current owner-evaluated metric was supplied; locked historical focus evidence is not treated as a live measurement.");
        }
        if (liveMetric.Evidence is null)
        {
            return GateResult.Unknown(code + "_LIVE_METRIC", "The current metric evidence payload is unavailable.");
        }
        var evidence = liveMetric.Evidence;
        if (liveMetric.Disposition == GateDisposition.Failed)
        {
            return GateResult.Fail(code + "_LIVE_METRIC", "The current focus-domain owner reported that its frame metric failed.");
        }
        if (liveMetric.Disposition != GateDisposition.Passed)
        {
            return GateResult.Unknown(code + "_LIVE_METRIC", "The current focus-domain owner did not report a passing frame metric.");
        }
        if (expected.Metric is null || evidence.Kind != expected.Metric.Kind ||
            !string.Equals(evidence.SourceCameraStableDeviceId, expected.Metric.SourceCameraStableDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return GateResult.Fail(code + "_LIVE_METRIC", "The current metric kind/source camera belongs to a different focus domain.");
        }
        if (!double.IsFinite(evidence.Value) || evidence.Value <= 0 || string.IsNullOrWhiteSpace(evidence.Unit) || !IsSha256(evidence.EvidenceSha256))
        {
            return GateResult.Fail(code + "_LIVE_METRIC", "The current metric lacks a finite positive value, unit, or immutable evidence SHA-256.");
        }
        if (liveMetric.VerifiedUtc < setup.LockedUtc || liveMetric.VerifiedUtc > evaluatedUtc.AddMinutes(5))
        {
            return GateResult.Fail(code + "_LIVE_METRIC", $"The current metric timestamp {liveMetric.VerifiedUtc:O} is not from this locked Night Setup at evaluation time {evaluatedUtc:O}.");
        }
        if (liveMetric.ValidUntilUtc <= liveMetric.VerifiedUtc || evaluatedUtc > liveMetric.ValidUntilUtc)
        {
            return GateResult.Fail(code + "_LIVE_METRIC", $"The current metric validity ended at {liveMetric.ValidUntilUtc:O}; locked historical values are not substituted.");
        }
        return GateResult.Pass(
            code + "_LIVE_METRIC",
            $"The current {evidence.Kind} metric from '{evidence.SourceCameraStableDeviceId}' is valid through {liveMetric.ValidUntilUtc:O}.",
            new Dictionary<string, double> { ["value"] = evidence.Value });
    }

    private static bool IsSha256(string value)
    {
        var normalized = (value ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit);
    }

    private static void AddIdentity(List<GateResult> gates, string code, string expected, string actual) =>
        gates.Add(string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
            ? GateResult.Pass(code, $"Identity matched: {actual}.")
            : GateResult.Fail(code, $"Identity mismatch; expected '{expected}', actual '{actual}'."));

    private static void AddEquality<T>(List<GateResult> gates, string code, T expected, T actual, string label) where T : IEquatable<T> =>
        gates.Add(expected.Equals(actual)
            ? GateResult.Pass(code, $"{label} matched: {actual}.")
            : GateResult.Fail(code, $"{label} mismatch; expected {expected}, actual {actual}."));
}

public enum CalibrationBlockKind
{
    WavelengthReferenceBefore,
    LedFlat,
    ScienceTarget,
    WavelengthReferenceAfter
}

public sealed record ObservationQueueEntry(
    CalibrationBlockKind Kind,
    EquatorialTarget Target,
    TimeSpan Duration,
    string SetupId,
    string Purpose);

public sealed record NightObservationQueue(
    string QueueId,
    NightSetupRecord Setup,
    IReadOnlyList<ObservationQueueEntry> Entries)
{
    public IReadOnlyList<string> Validate()
    {
        var issues = Setup.Validate().ToList();
        if (Entries.Count == 0) issues.Add("Observation queue is empty.");
        if (Entries.Any(entry => !string.Equals(entry.SetupId, Setup.NightSetupId, StringComparison.Ordinal))) issues.Add("Every queue entry must use the locked Night Setup ID.");
        if (!Entries.Any(entry => entry.Kind == CalibrationBlockKind.ScienceTarget)) issues.Add("Observation queue has no science target.");
        if (!Entries.Any(entry => entry.Kind is CalibrationBlockKind.WavelengthReferenceBefore or CalibrationBlockKind.WavelengthReferenceAfter)) issues.Add("Observation queue has no wavelength reference.");
        return issues.AsReadOnly();
    }
}
