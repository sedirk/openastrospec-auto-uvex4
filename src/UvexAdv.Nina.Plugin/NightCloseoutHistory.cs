using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UvexAdv.Nina.Plugin;

// History only: no owner, commands, lease, or predicates used by the live safety chain.
internal static class NightCloseoutHistory
{
    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVEX-ADV", "nights");
    private const string Pointer = "last-real-night.txt";
    internal const string Unverified = "上次记录不能证明已关顶。旧会话的安全待机不会随重启恢复。请现场核对屋顶、设备停止状态和安全监视器；不会自动续拍或补发机械动作。";

    // Written once, under the reserved real night lease, before running any child.
    internal static async Task RememberRealNightAsync(string path)
    {
        try
        {
            var pointer = Path.Combine(Path.GetDirectoryName(path)!, Pointer);
            await File.WriteAllTextAsync(pointer + ".tmp", Path.GetFileName(path)).ConfigureAwait(false);
            File.Move(pointer + ".tmp", pointer, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The authoritative night journal is already written. An advisory
            // index failure cannot cancel a night or trigger mechanical cleanup.
            NINA.Core.Utility.Logger.Warning("上次真实整夜日志索引未更新：" + ex.Message);
        }
    }

    internal static string ReadNotice(string directory, DateTimeOffset now)
    {
        try
        {
            if (!Directory.Exists(directory)) return "";
            var pointer = Path.Combine(directory, Pointer);
            string? name = null;
            if (File.Exists(pointer))
            {
                if (new FileInfo(pointer).Length > 128 || !IsJournalName(name = File.ReadAllText(pointer), "night")) return Unverified;
            }
            // Catch legacy journals and newer nights interrupted before indexing.
            // Never scan the full archive or follow arbitrary journal paths.
            for (var day = 0; day < 3; day++)
            {
                var prefix = now.UtcDateTime.AddDays(-day).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
                var paths = Directory.EnumerateFiles(directory, $"night-{prefix}T*.json").Take(65).ToArray();
                if (paths.Length > 64) return Unverified;
                foreach (var path in paths.OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
                {
                    if (!IsJournalName(Path.GetFileName(path), "night")) continue;
                    if (name is not null && string.CompareOrdinal(Path.GetFileName(path), name) <= 0)
                        return Inspect(Path.Combine(directory, name), knownReal: true) ?? Unverified;
                    var notice = Inspect(path, knownReal: false);
                    if (notice is not null) return notice;
                }
            }
            return name is null ? "" : Inspect(Path.Combine(directory, name), knownReal: true) ?? Unverified;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        { return Unverified; }
    }

    private static bool IsJournalName(string name, string kind) =>
        Regex.IsMatch(name, @"\A" + kind + @"-[0-9]{8}T[0-9]{9}Z-[a-fA-F0-9]{32}\.json\z", RegexOptions.CultureInvariant);
    private static JsonDocument Read(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length is 0 or > 4_194_304) throw new InvalidDataException("Night journal size invalid.");
        return JsonDocument.Parse(file);
    }
    private static bool True(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    private static string? Str(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // null is an explicitly simulated night, "" is a checked closeout.
    private static string? Inspect(string path, bool knownReal)
    {
        using var doc = Read(path);
        var root = doc.RootElement;
        if (root.TryGetProperty("physicalHardware", out var mode) && mode.ValueKind == JsonValueKind.False)
            return knownReal ? Unverified : null;
        if (!root.TryGetProperty("outcomes", out var items) || items.ValueKind != JsonValueKind.Array)
            return Unverified;
        var terminal = items.EnumerateArray().LastOrDefault(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("terminal", out _));
        if (terminal.ValueKind != JsonValueKind.Object) return Unverified;
        if (!True(terminal, "physicalHardware"))
            return !knownReal && terminal.TryGetProperty("physicalHardware", out var t) && t.ValueKind == JsonValueKind.False ? null : Unverified;
        if (Str(root, "phase") is not ("Terminal" or "NativeEnd")) return Unverified;
        if (True(terminal, "physicalCloseoutConfirmed")) return "";
        var standbyPath = Str(terminal, "standbyJournalPath");
        if (!True(terminal, "handedToStandby") || string.IsNullOrEmpty(standbyPath) ||
            !IsJournalName(Path.GetFileName(standbyPath), "standby") ||
            !string.Equals(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(standbyPath)), standbyPath, StringComparison.OrdinalIgnoreCase))
            return Unverified;
        using var watch = Read(standbyPath);
        if (!string.Equals(Str(watch.RootElement, "parentJournal"), path, StringComparison.OrdinalIgnoreCase) ||
            !watch.RootElement.TryGetProperty("current", out var current) || !True(current, "PhysicalHardware") ||
            !string.Equals(Str(current, "Id"), Path.GetFileNameWithoutExtension(standbyPath), StringComparison.Ordinal) ||
            Str(current, "Phase") != "Closed" ||
            !current.TryGetProperty("Errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0)
            return Unverified;
        return "";
    }
}
