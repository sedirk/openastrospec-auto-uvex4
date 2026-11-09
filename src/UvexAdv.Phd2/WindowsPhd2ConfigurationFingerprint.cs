using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace UvexAdv.Phd2;

/// <summary>Read-only, local-owner evidence. Only the documented screen Gamma
/// value is excluded; camera, algorithms, calibration, profile selection and
/// every unknown setting remain material. Never exports registry values.</summary>
public static class WindowsPhd2ConfigurationFingerprint
{
    [SupportedOSPlatform("windows")]
    public static string? Read(int expectedProfileId)
        => ReadValues(expectedProfileId) is { } values ? Compute(values, expectedProfileId) : null;

    // A native single frame temporarily writes camera/gain and restores it.
    // This witness is scoped to that RPC only; binning and every other setting
    // remain material. It never changes the registry or exports its values.
    [SupportedOSPlatform("windows")]
    public static Func<bool>? CreateNativeCaptureWitness(int profileId, Phd2SingleFrameRequest request)
    {
        var before = ReadValues(profileId);
        var second = ReadValues(profileId);
        if (before is null || second is null || Compute(before, profileId) != Compute(second, profileId)) return null;
        return () => ReadValues(profileId) is { } first && ReadValues(profileId) is { } next &&
            Compute(first, profileId) == Compute(next, profileId) &&
            IsOnlyNativeGainChange(before, next, profileId, request.GainPercent);
    }

    public static bool IsOnlyNativeGainChange(IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> current, int profileId, int requestedGain)
    {
        var key = $"profile/{profileId}/camera/gain";
        if (requestedGain is < 0 or > 100 || !before.TryGetValue(key, out var original) ||
            !current.TryGetValue(key, out var gain) ||
            (gain != original && gain != "4:" + requestedGain.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            return false;
        var normalized = current.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        normalized[key] = original;
        return Compute(before, profileId) == Compute(normalized, profileId);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyDictionary<string, string>? ReadValues(int expectedProfileId)
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\StarkLabs\PHDGuidingV2", false);
        if (root is null || !int.TryParse(root.GetValue("currentProfile")?.ToString(), out var active) ||
            active != expectedProfileId) return null;
        using var camera = root.OpenSubKey($@"profile\{active}\camera", false);
        using var scope = root.OpenSubKey($@"profile\{active}\scope", false);
        if (camera is null || scope is null) return null;
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        ReadTree(root, "", values, 0);
        return values;
    }

    public static string Compute(IReadOnlyDictionary<string, string> values, int profileId)
    {
        var gamma = $"profile/{profileId}/Gamma";
        var material = values.Where(pair => !string.Equals(pair.Key, gamma, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(material)));
    }

    [SupportedOSPlatform("windows")]
    private static void ReadTree(RegistryKey key, string path, IDictionary<string, string> values, int depth)
    {
        if (depth > 24 || values.Count > 10000) throw new IOException("PHD2 configuration evidence exceeds its bound.");
        foreach (var name in key.GetValueNames())
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                ?? throw new IOException("PHD2 configuration changed while being read.");
            values[path + name] = ((int)key.GetValueKind(name)) + ":" + JsonSerializer.Serialize(value, value.GetType());
        }
        foreach (var child in key.GetSubKeyNames())
        {
            using var subkey = key.OpenSubKey(child, false)
                ?? throw new IOException("PHD2 configuration key disappeared while being read.");
            values[path + child + "/"] = "key";
            ReadTree(subkey, path + child + "/", values, depth + 1);
        }
    }
}
