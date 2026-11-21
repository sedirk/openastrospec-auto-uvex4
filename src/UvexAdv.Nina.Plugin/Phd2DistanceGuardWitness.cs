using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UvexAdv.Phd2;

namespace UvexAdv.Nina.Plugin;

// Star::STAR_ERROR (8) is generic. Its SNR is NOT a distance-guard receipt.
// PHD2's native debug trace is optional; absence, ambiguity or rotation fails closed.
internal sealed record Phd2DistanceGuardWitness(long Frame, Phd2Point Primary, double Offset, double Threshold)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string N = @"(-?\d+(?:\.\d+)?)";
    private static readonly Regex Prefix = new(@"^(\d\d:\d\d:\d\d\.\d{3})\s+\S+\s+(\d+)\s+(.+)$");
    private static readonly Regex Set = new(@"""method""\s*:\s*""set_lock_position"".*?""x""\s*:\s*" + N +
        @".*?""y""\s*:\s*" + N + @".*?""exact""\s*:\s*true");
    private static readonly Regex Find = new(@"^Star::Find\(.*\) frame (\d+)\s*$");
    private static readonly Regex Found = new(@"^Star::Find returns 1 \([01]\), X=" + N + ", Y=" + N +
        @", Mass=\S+, SNR=" + N + @", Peak=\S+ HFD=" + N);
    private static readonly Regex Reject = new(@"^DistanceChecker: reject for large offset \(" + N + " > " + N + @"\)");

    internal static Phd2DistanceGuardWitness? FromLocalOwner(string host, Phd2ExactLockPositionResult exact,
        Phd2GuideStep lost, DateTimeOffset eventUtc, Phd2Point previousPrimary, CancellationToken token)
    {
        if (!(host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
              IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))) return null;
        var processes = Process.GetProcessesByName("phd2");
        try
        {
            if (processes.Length != 1) return null;
            var threads = processes[0].Threads.Cast<ProcessThread>().Select(t => t.Id).ToHashSet();
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PHD2");
            if (!Path.IsPathFullyQualified(dir) || dir.StartsWith(@"\\", StringComparison.Ordinal) ||
                new DriveInfo(Path.GetPathRoot(dir)!).DriveType != DriveType.Fixed) return null;
            for (var parent = new DirectoryInfo(dir); parent is not null; parent=parent.Parent)
            {
                token.ThrowIfCancellationRequested();
                if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            }
            // Never tail an arbitrary newest file, a different instance, a whole night, or a remote path.
            var candidates = new DirectoryInfo(dir).EnumerateFiles("PHD2_DebugLog_*.txt").Take(257).ToArray();
            if (candidates.Length > 256) return null;
            var files = candidates
                .Where(f => f.LastWriteTimeUtc >= eventUtc.UtcDateTime.AddSeconds(-3) &&
                            f.LastWriteTimeUtc <= DateTime.UtcNow.AddSeconds(1)).Take(2).ToArray();
            if (files.Length != 1 || files[0].Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            using var file = new FileStream(files[0].FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var end = file.Length;
            var bytes = new byte[(int)Math.Min(end, 64 * 1024)];
            file.Position = end - bytes.Length;
            token.ThrowIfCancellationRequested();
            file.ReadExactly(bytes);
            token.ThrowIfCancellationRequested();
            return Parse(Encoding.UTF8.GetString(bytes), threads, exact, lost, eventUtc, previousPrimary);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or System.Security.SecurityException) { return null; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static Phd2DistanceGuardWitness? Parse(string tail, IReadOnlySet<int> ownerThreads,
        Phd2ExactLockPositionResult exact, Phd2GuideStep lost, DateTimeOffset eventUtc, Phd2Point previousPrimary)
    {
        if (lost is not { ErrorCode: 8, Frame: > 0, Snr: > 0, HfdPixels: > 0 } || !exact.Exact ||
            eventUtc < exact.CompletedUtc || eventUtc - exact.CompletedUtc > TimeSpan.FromSeconds(60)) return null;
        static double D(Match m, int n) => double.Parse(m.Groups[n].Value, Inv);
        var thread = -1; var frame = false; Phd2Point? primary = null; Phd2DistanceGuardWitness? result = null;
        foreach (var line in tail.Split('\n'))
        {
            var p = Prefix.Match(line.TrimEnd('\r'));
            if (!p.Success || !TimeSpan.TryParseExact(p.Groups[1].Value, @"hh\:mm\:ss\.fff", Inv, out var time) ||
                !int.TryParse(p.Groups[2].Value, out var tid) || !ownerThreads.Contains(tid)) continue;
            // Associate local time-of-day with the event, including a stage crossing midnight.
            var delta = time - eventUtc.LocalDateTime.TimeOfDay;
            if (delta.TotalHours > 12) delta -= TimeSpan.FromDays(1);
            if (delta.TotalHours < -12) delta += TimeSpan.FromDays(1);
            var stamp = eventUtc + delta;
            if (stamp < exact.CompletedUtc.AddSeconds(-2) || stamp > eventUtc.AddSeconds(1)) continue;
            var msg = p.Groups[3].Value;
            if (msg.Contains("\"set_lock_position\"", StringComparison.Ordinal))
            {
                var set = Set.Match(msg);
                if (thread != -1 || !set.Success || stamp > exact.CompletedUtc.AddMilliseconds(100) ||
                    Distance(new(D(set,1), D(set,2)), exact.Verified) > .025) return null;
                thread = tid; continue;
            }
            if (tid != thread) continue;
            if (msg.StartsWith("Star::Find(", StringComparison.Ordinal))
            {
                var find = Find.Match(msg);
                frame = find.Success && long.TryParse(find.Groups[1].Value, out var number) && number == lost.Frame;
                primary = null; continue;
            }
            if (!frame) continue;
            if (msg.StartsWith("Star::Find returns", StringComparison.Ordinal))
            {
                var m = Found.Match(msg);
                if (primary is not null || !m.Success || !(Math.Abs(D(m,3) - lost.Snr.Value) <= .11) ||
                    !(Math.Abs(D(m,4) - lost.HfdPixels.Value) <= .11)) return null;
                primary = new(D(m,1), D(m,2));
                if (!(Distance(primary, previousPrimary) <= 6)) return null;
            }
            var r = Reject.Match(msg);
            if (!r.Success) continue;
            if (result is not null || primary is null || Math.Abs(delta.TotalSeconds) > 2 ||
                !(D(r,1) > D(r,2) && D(r,2) > 0) || !(Math.Abs(Distance(primary,exact.Verified) - D(r,1)) <= .1)) return null;
            result = new(lost.Frame.Value, primary, D(r,1), D(r,2));
            frame = false;
        }
        return result;
    }

    internal static double Distance(Phd2Point a, Phd2Point b) => Math.Sqrt(Math.Pow(a.X-b.X,2) + Math.Pow(a.Y-b.Y,2));
}
