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
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\StarkLabs\PHDGuidingV2", false);
        if (root is null || !int.TryParse(root.GetValue("currentProfile")?.ToString(), out var active) ||
            active != expectedProfileId) return null;
        using var camera = root.OpenSubKey($@"profile\{active}\camera", false);
        using var scope = root.OpenSubKey($@"profile\{active}\scope", false);
        if (camera is null || scope is null) return null;
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        ReadTree(root, "", values, 0);
        return Compute(values, expectedProfileId);
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
