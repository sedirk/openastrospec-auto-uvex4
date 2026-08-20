using System.IO;
using System.Text.Json;

namespace UvexAdv.Nina.Plugin;

internal static class LoopRunLogger
{
    public static async Task WriteAsync(string kind, object result, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UVEX-ADV", "closed-loop");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
        var line = JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow, kind, result }) + Environment.NewLine;
        await File.AppendAllTextAsync(path, line, cancellationToken).ConfigureAwait(false);
    }
}
