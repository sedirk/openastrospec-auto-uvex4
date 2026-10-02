using System.IO;
using System.Text.Json;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

internal sealed record NativeMeridianJournal(string RunId, string Phase, string BeforeSide, string AfterSide,
    DateTimeOffset UpdatedUtc, IReadOnlyList<string> Segments);

internal static class NativeMeridianJournalStore
{
    internal static GateResult? PendingGate(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<NativeMeridianJournal>(File.ReadAllText(path));
            if (entry is { Phase: "MountVerified" } && !string.IsNullOrWhiteSpace(entry.RunId) &&
                entry.BeforeSide is "pierEast" or "pierWest" && entry.AfterSide is "pierEast" or "pierWest" &&
                entry.AfterSide != entry.BeforeSide) return null;
            return GateResult.Unknown("NATIVE_FLIP_UNCONFIRMED",
                "上次中天翻转没有完整的镜筒侧/停止读回；不自动重发翻转、重新入缝或启动曝光。请核验赤道仪现场状态并处理翻转记录。" );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return GateResult.Unknown("NATIVE_FLIP_JOURNAL_UNREADABLE", $"翻转记录不可读，未授权机械动作：{ex.Message}");
        }
    }

    internal static async Task SaveAsync(string path, NativeMeridianJournal entry, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        // This is a control journal, not an observation. Flush before dispatch;
        // never modify/move any FITS or historical segment manifest.
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, entry, cancellationToken: token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
