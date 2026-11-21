using System.IO;
using System.Text.Json;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// One receipt belongs to the explicitly started NIGHT (not a target/runner).
// Never deserialize an old result as current mount position/authority.
internal sealed class NightStartMountHome(string nightJournal)
{
    private int state; // not attempted, charged (including uncertain), verified
    internal bool Verified => Volatile.Read(ref state) == 2;
    internal bool Unconfirmed => Volatile.Read(ref state) == 1;
    internal string Phase => Volatile.Read(ref state) switch { 0 => "Pending", 2 => "Verified", _ => "ReservedOrUnconfirmed" };
    private string IntentPath => Path.Combine(Path.GetDirectoryName(nightJournal)!, "start-home",
        Path.GetFileNameWithoutExtension(nightJournal) + ".intent.json");

    internal async Task<GateResult> EnsureAsync(string runId, string mountId, string configHash,
        Func<CancellationToken, Task<GateResult>> boundary, Func<CancellationToken, Task<GateResult>> home,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Verified) return GateResult.Pass("NIGHT_START_HOME_ALREADY_VERIFIED", "本轮已完成开夜回零；不再归零。");
        GateResult Unconfirmed() => GateResult.Unknown("NIGHT_START_HOME_UNCONFIRMED",
            "本轮已有开夜回零意图，但未能确认完成。不再回零或转向；请核对赤道仪和停止状态。");
        if (Volatile.Read(ref state) != 0 || File.Exists(IntentPath)) return Unconfirmed();
        var gate = await boundary(token).ConfigureAwait(false);
        if (gate.Disposition != GateDisposition.Passed) return gate;
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return Unconfirmed();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(IntentPath)!);
            await WriteNewAsync(IntentPath, new { schemaVersion = 1, night = Path.GetFileName(nightJournal),
                runId, mountId, configHash, utc = DateTimeOffset.UtcNow, maxFindHome = 1 }, token).ConfigureAwait(false);
            // Intent persistence and native/RPC latency invalidate old safety proof.
            gate = await boundary(token).ConfigureAwait(false);
            if (gate.Disposition != GateDisposition.Passed) return gate;
            token.ThrowIfCancellationRequested();
            var result = await home(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.Disposition != GateDisposition.Passed || result.Code != "NIGHT_HOME_VERIFIED")
                return GateResult.Unknown("NIGHT_START_HOME_UNCONFIRMED", $"开夜回零未确认：{result.Code} — {result.Message} 不发目录转向。", result.Metrics);
            gate = await boundary(token).ConfigureAwait(false);
            if (gate.Disposition != GateDisposition.Passed) return gate;
            await WriteNewAsync(Path.ChangeExtension(IntentPath, "verified.json"),
                new { schemaVersion = 1, runId, mountId, configHash, utc = DateTimeOffset.UtcNow, result }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref state, 2);
            return GateResult.Pass("NIGHT_START_HOME_VERIFIED", "本轮开夜回零已由原设备两次确认停止到位；仍须正常验证跟踪和新图。", result.Metrics);
        }
        catch (IOException ex) { return GateResult.Unknown("NIGHT_START_HOME_RECORD_UNCONFIRMED", $"开夜回零记录无法独占写入或核对：{ex.Message} 不重试回零或转向。"); }
        catch (UnauthorizedAccessException ex) { return GateResult.Unknown("NIGHT_START_HOME_RECORD_UNCONFIRMED", $"开夜回零记录权限不足：{ex.Message} 不重试回零或转向。"); }
    }

    private static async Task WriteNewAsync(string path, object record, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(file, record, cancellationToken: token).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }
}
