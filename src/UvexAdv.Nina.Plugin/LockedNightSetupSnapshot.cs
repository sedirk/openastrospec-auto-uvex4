using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UvexAdv.Core;
using UvexAdv.Observatory;
using UvexAdv.Phd2;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Nina.Plugin;

internal sealed record LoadedNightSetupSnapshot(
    NightSetupRecord Value,
    string AbsolutePath,
    string Sha256);

/// <summary>
/// Loads the immutable per-night contract by exact content hash. The Profile is
/// only a binding to that contract; changing a Profile value cannot silently
/// change an active real observation.
/// </summary>
internal static class LockedNightSetupSnapshotLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<(LoadedNightSetupSnapshot? Snapshot, IReadOnlyList<string> Issues)> LoadAsync(
        RealRunConfiguration configuration,
        ObservationPlan plan,
        LoadedCommissioningPreset commissioning,
        CancellationToken cancellationToken)
    {
        var binding = configuration.NightSetup;
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(binding.SnapshotPath)) issues.Add("Night Setup snapshot path is required.");
        if (string.IsNullOrWhiteSpace(binding.SnapshotSha256)) issues.Add("Night Setup snapshot SHA-256 is required.");
        if (issues.Count > 0) return (null, issues);

        string path;
        try { path = Path.GetFullPath(binding.SnapshotPath); }
        catch (Exception ex) { return (null, [$"Night Setup snapshot path is invalid: {ex.Message}"]); }
        if (!File.Exists(path)) return (null, [$"Night Setup snapshot does not exist: {path}"]);

        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { return (null, [$"Night Setup snapshot could not be read: {ex.Message}"]); }

        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        if (!SameHash(sha256, binding.SnapshotSha256))
        {
            return (null, [$"Night Setup snapshot SHA-256 mismatch. Actual {sha256}."]);
        }
        if (!SameHash(sha256, commissioning.Value.NightSetupSha256))
        {
            return (null, ["Night Setup snapshot is not the one bound into the commissioning preset."]);
        }

        NightSetupRecord? setup;
        try { setup = JsonSerializer.Deserialize<NightSetupRecord>(bytes, JsonOptions); }
        catch (Exception ex) { return (null, [$"Night Setup snapshot JSON is invalid: {ex.Message}"]); }
        if (setup is null) return (null, ["Night Setup snapshot JSON is empty."]);

        issues.AddRange(setup.Validate());
        ValidateBindings(configuration, plan, commissioning, setup, issues);
        return issues.Count == 0
            ? (new LoadedNightSetupSnapshot(setup, path, sha256), issues)
            : (null, issues);
    }

    public static IReadOnlyList<GateResult> EvaluateLive(
        LoadedNightSetupSnapshot snapshot,
        object atr,
        UvexDeviceStatus uvex,
        Phd2ProfileBindingSnapshot phdEvidence,
        QhyCameraStatus qhy,
        IReadOnlyList<LiveFocusDomainState>? focusDomains = null,
        DateTimeOffset? evaluatedUtc = null)
    {
        var setup = snapshot.Value;
        var atrId = ReadString(atr, "DeviceId");
        var gain = ReadInt32(atr, "Gain");
        var offset = ReadInt32(atr, "Offset");
        var binX = ReadInt32(atr, "BinX");
        var binY = ReadInt32(atr, "BinY");
        var temperature = ReadDouble(atr, "Temperature");
        var readout = ReadString(atr, "ReadoutMode");
        if (string.IsNullOrWhiteSpace(readout)) readout = ReadString(atr, "ReadoutModeForNormalImages");
        var live = new LiveSetupState(
            atrId,
            gain ?? int.MinValue,
            offset ?? int.MinValue,
            binX ?? int.MinValue,
            binY ?? int.MinValue,
            temperature is { } measuredTemperature && double.IsFinite(measuredTemperature) ? measuredTemperature : null,
            readout,
            uvex.SlitPosition ?? int.MinValue,
            uvex.GratingPositionSteps ?? int.MinValue,
            uvex.FocusPositionSteps ?? int.MinValue,
            phdEvidence.ProfileName,
            phdEvidence.CameraStableIds.SingleOrDefault() ?? string.Empty,
            qhy.Identity?.StableId ?? string.Empty,
            focusDomains);
        var gates = NightSetupCompatibility.Evaluate(
            setup,
            live,
            evaluatedUtc: evaluatedUtc).ToList();
        gates.Add(string.IsNullOrWhiteSpace(readout)
            ? GateResult.Unknown("ATR_READOUT_MODE", "N.I.N.A. does not expose the active ATR readout mode; it cannot be inferred from the expected value.")
            : string.Equals(readout, setup.Atr585m.ReadoutMode, StringComparison.Ordinal)
                ? GateResult.Pass("ATR_READOUT_MODE", $"ATR readout mode matched: {readout}.")
                : GateResult.Fail("ATR_READOUT_MODE", $"ATR readout mode is '{readout}', expected '{setup.Atr585m.ReadoutMode}'."));

        var roi = ReadAtrRoi(atr);
        gates.Add(roi is null
            ? GateResult.Unknown("ATR_ROI", "N.I.N.A. does not expose an attestable active ATR ROI before acquisition; expected ROI values are not treated as measurements.")
            : roi.Value.X == setup.Atr585m.RoiX && roi.Value.Y == setup.Atr585m.RoiY &&
              roi.Value.Width == setup.Atr585m.RoiWidth && roi.Value.Height == setup.Atr585m.RoiHeight
                ? GateResult.Pass("ATR_ROI", $"ATR ROI matched: {roi.Value.X},{roi.Value.Y} {roi.Value.Width}x{roi.Value.Height}.")
                : GateResult.Fail("ATR_ROI", $"ATR ROI is {roi.Value.X},{roi.Value.Y} {roi.Value.Width}x{roi.Value.Height}; expected {setup.Atr585m.RoiX},{setup.Atr585m.RoiY} {setup.Atr585m.RoiWidth}x{setup.Atr585m.RoiHeight}."));
        return gates;
    }

    private static void ValidateBindings(
        RealRunConfiguration configuration,
        ObservationPlan plan,
        LoadedCommissioningPreset commissioning,
        NightSetupRecord setup,
        List<string> issues)
    {
        if (!string.Equals(setup.NightSetupId, plan.NightSetupId, StringComparison.Ordinal)) issues.Add("Night Setup ID does not match the observation plan.");
        if (!string.Equals(setup.NightSetupId, commissioning.Value.NightSetupId, StringComparison.Ordinal)) issues.Add("Night Setup ID does not match the commissioning preset.");

        var atr = setup.Atr585m;
        if (!string.Equals(atr.StableDeviceId, plan.ExpectedAtrCameraId, StringComparison.Ordinal)) issues.Add("Night Setup ATR identity does not match the plan.");
        if (atr.Gain != configuration.Atr.Gain || atr.Offset != configuration.Atr.Offset || atr.BinningX != configuration.Atr.Binning || atr.BinningY != configuration.Atr.Binning) issues.Add("Night Setup ATR gain/offset/binning does not match the locked run binding.");
        if (atr.TemperatureC is not { } atrTemperature || !Same(atrTemperature, configuration.Atr.TargetTemperatureC)) issues.Add("Night Setup ATR target temperature does not match the locked run binding.");
        if (!short.TryParse(atr.ReadoutMode, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var readoutIndex) ||
            readoutIndex != configuration.Atr.ReadoutModeIndex)
        {
            issues.Add("Night Setup ATR readout-mode index does not match the Profile binding.");
        }
        if (atr.RoiX != configuration.Atr.Roi.X || atr.RoiY != configuration.Atr.Roi.Y || atr.RoiWidth != configuration.Atr.Roi.Width || atr.RoiHeight != configuration.Atr.Roi.Height) issues.Add("Night Setup ATR ROI does not match the locked run binding.");

        if (!string.Equals(setup.G3StableDeviceId, configuration.Phd2.CameraStableId, StringComparison.OrdinalIgnoreCase)) issues.Add("Night Setup G3 USB identity does not match the PHD2 evidence binding.");
        if (!string.Equals(setup.Phd2ProfileName, configuration.Phd2.ProfileName, StringComparison.Ordinal)) issues.Add("Night Setup PHD2 profile does not match the locked run binding.");
        if (setup.G3SaturationAdu != configuration.G3.SaturationAdu) issues.Add("Night Setup G3 saturation ADU does not match the locked run binding.");

        var qhy = setup.QhyMiniCam8m;
        if (!string.Equals(qhy.StableDeviceId, plan.ExpectedQhyCameraId, StringComparison.Ordinal)) issues.Add("Night Setup QHY identity does not match the plan.");
        if (qhy.Gain != configuration.Qhy.Gain || qhy.Offset != configuration.Qhy.Offset || qhy.BinningX != configuration.Qhy.Binning || qhy.BinningY != configuration.Qhy.Binning) issues.Add("Night Setup QHY gain/offset/binning does not match the locked run binding.");
        if (!string.Equals(qhy.ReadoutMode, configuration.Qhy.ReadoutMode.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)) issues.Add("Night Setup QHY readout mode does not match the locked run binding.");
        if (qhy.RoiX != configuration.Qhy.RoiX || qhy.RoiY != configuration.Qhy.RoiY || qhy.RoiWidth != configuration.Qhy.RoiWidth || qhy.RoiHeight != configuration.Qhy.RoiHeight) issues.Add("Night Setup QHY ROI does not match the locked run binding.");
        var configuredQhyTemperature = configuration.Qhy.TargetTemperatureC;
        if (!NullableSame(qhy.TemperatureC, configuredQhyTemperature)) issues.Add("Night Setup QHY target temperature does not match the Profile binding.");

        if (setup.SlitPosition != configuration.ExpectedUvexSlitPosition ||
            setup.GratingPositionSteps != configuration.ExpectedUvexGratingPositionSteps ||
            setup.M2PositionSteps != configuration.ExpectedUvexM2PositionSteps)
        {
            issues.Add("Night Setup UVEX slit/grating/M2 does not match the Profile binding.");
        }

        if (!Same(setup.HorizonPolicy.BaseMinimumAltitudeDegrees, plan.Horizon.BaseMinimumAltitudeDegrees) ||
            !Same(setup.HorizonPolicy.StartMarginDegrees, plan.Horizon.StartMarginDegrees) ||
            !Same(setup.HorizonPolicy.ContinueMarginDegrees, plan.Horizon.ContinueMarginDegrees))
        {
            issues.Add("Night Setup horizon policy does not match the observation plan.");
        }
        if (string.IsNullOrWhiteSpace(setup.SafetyCapability)) issues.Add("Night Setup safety capability evidence is missing.");
    }

    private static bool SameHash(string left, string right) =>
        string.Equals(NormalizeHash(left), NormalizeHash(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHash(string value) => value.Replace("-", string.Empty, StringComparison.Ordinal).Trim();
    private static bool Same(double left, double right) => double.IsFinite(left) && double.IsFinite(right) && Math.Abs(left - right) <= 1e-9;
    private static bool NullableSame(double? left, double? right) => left is null && right is null || left is { } l && right is { } r && Same(l, r);

    private static string ReadString(object source, string name) =>
        source.GetType().GetProperty(name)?.GetValue(source)?.ToString() ?? string.Empty;

    private static int? ReadInt32(object source, string name) =>
        source.GetType().GetProperty(name)?.GetValue(source) switch
        {
            int value => value,
            short value => value,
            _ => null,
        };

    private static double? ReadDouble(object source, string name) =>
        source.GetType().GetProperty(name)?.GetValue(source) switch
        {
            double value => value,
            float value => value,
            _ => null,
        };

    private static (int X, int Y, int Width, int Height)? ReadAtrRoi(object source)
    {
        if (ReadBoolean(source, "IsSubSampleEnabled") == false)
        {
            var sensorWidth = ReadInt32(source, "XSize");
            var sensorHeight = ReadInt32(source, "YSize");
            return sensorWidth is { } fullWidth && sensorHeight is { } fullHeight
                ? (0, 0, fullWidth, fullHeight)
                : null;
        }
        var x = ReadInt32(source, "SubSampleX") ?? ReadInt32(source, "RoiX");
        var y = ReadInt32(source, "SubSampleY") ?? ReadInt32(source, "RoiY");
        var width = ReadInt32(source, "SubSampleWidth") ?? ReadInt32(source, "RoiWidth");
        var height = ReadInt32(source, "SubSampleHeight") ?? ReadInt32(source, "RoiHeight");
        return x is { } actualX && y is { } actualY && width is { } actualWidth && height is { } actualHeight
            ? (actualX, actualY, actualWidth, actualHeight)
            : null;
    }

    private static bool? ReadBoolean(object source, string name) =>
        source.GetType().GetProperty(name)?.GetValue(source) as bool?;
}
