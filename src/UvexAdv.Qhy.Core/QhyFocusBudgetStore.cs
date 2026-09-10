using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UvexAdv.Qhy.Core;

/// <summary>Per-run durable position obligation. Reopening a worker never grants
/// another motion allowance. Only the same policy/configuration may read it.</summary>
public static class QhyFocusBudgetStore
{
    private sealed record Saved(int Version, string RunId, string ConfigurationSha256,
        QhyFocusRunPolicy Policy, QhyFocusBudgetState State);

    public static QhyFocusBudget Open(string directory, string runId, string configurationSha256, QhyFocusRunPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))) + ".json");
        Saved? saved = null;
        if (Directory.Exists(directory))
        {
            foreach (var existing in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (new FileInfo(existing).Length > 65536) throw new InvalidDataException("PHOTOMETRY_FOCUS_LEDGER_INVALID: Oversized record.");
                var candidate = JsonSerializer.Deserialize<Saved>(File.ReadAllText(existing))
                    ?? throw new InvalidDataException("PHOTOMETRY_FOCUS_LEDGER_INVALID: Empty record.");
                if (candidate.Version != 1 || candidate.State is null || candidate.Policy is null)
                    throw new InvalidDataException("PHOTOMETRY_FOCUS_LEDGER_INVALID: Unknown record schema.");
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) saved = candidate;
                else if (candidate.State.Pending)
                    throw new InvalidOperationException("PHOTOMETRY_FOCUS_PENDING: Another run has an unresolved focus endpoint; do not erase its obligation by starting a new run.");
            }
        }
        if (saved is not null && (saved.RunId != runId || saved.ConfigurationSha256 != configurationSha256 || saved.Policy != policy))
            throw new InvalidOperationException("PHOTOMETRY_FOCUS_LEDGER_BINDING_CHANGED: Saved movement allowance belongs to another configuration.");
        return new QhyFocusBudget(policy, state =>
        {
            Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new Saved(1, runId, configurationSha256, policy, state));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }, saved?.State);
    }
}
