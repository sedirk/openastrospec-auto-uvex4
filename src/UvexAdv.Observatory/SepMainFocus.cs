using System.Text.Json;

namespace UvexAdv.Observatory;

public sealed record SepFocusReference(double X, double Y);
public sealed record SepFocusStar(int Id, double X, double Y, double R50, double R80, double Flux, double Snr);
public sealed record SepFocusFrame(SepFocusReference[] Reference, SepFocusStar[] Stars, string? Error = null)
{
    public void Validate(int width, int height)
    {
        if (Reference is null || Stars is null || Reference.Length > 30 || Stars.Length > 30
            || Reference.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.Y < 0 || p.X >= width || p.Y >= height)
            || Stars.Select(s => s.Id).Distinct().Count() != Stars.Length
            || Stars.Any(s => s.Id < 0 || s.Id >= Reference.Length || !double.IsFinite(s.X) || !double.IsFinite(s.Y)
                || s.X < 0 || s.Y < 0 || s.X >= width || s.Y >= height
                || !double.IsFinite(s.R50) || !double.IsFinite(s.R80) || s.R50 <= 0 || s.R80 < s.R50
                || !double.IsFinite(s.Flux) || s.Flux <= 0 || !double.IsFinite(s.Snr) || s.Snr < 15))
            throw new InvalidDataException("Invalid SEP fixed-aperture focus measurements");
    }
}

public sealed record SepMainFocusOptions
{
    public int MinimumPosition { get; init; }
    public int MaximumPosition { get; init; }
    public int Step { get; init; } = 50;
    public int HalfPoints { get; init; } = 2;
    public int Frames { get; init; } = 3;
    public int ExposureMilliseconds { get; init; } = 10000;
    public int GainPercent { get; init; } = 100;
    public double Saturation { get; init; } = 65520;
    public double MinimumAltitude { get; init; } = 42;
    public int MaximumSeconds { get; init; } = 900;

    public int[] Positions(int origin, int backlashIn, int backlashOut)
    {
        if (MinimumPosition < 0 || MaximumPosition <= MinimumPosition || Step is < 1 or > 150
            || HalfPoints is < 2 or > 4 || Frames is < 2 or > 5 || ExposureMilliseconds is < 500 or > 10000
            || GainPercent is < 0 or > 100 || !double.IsFinite(Saturation) || Saturation is <= 0 or > 65535
            || !double.IsFinite(MinimumAltitude) || MinimumAltitude is < 0 or > 89 || MaximumSeconds is < 60 or > 1800
            || backlashIn < 0 || backlashOut < 0)
            throw new ArgumentException("请配置有效的主镜对焦软限位、步长与曝光；软限位没有机器无关默认值。");
        var lower = (long)origin - (long)Step * HalfPoints - backlashIn;
        var upper = (long)origin + (long)Step * HalfPoints + backlashOut;
        if (lower < MinimumPosition || upper > MaximumPosition)
            throw new InvalidOperationException("扫焦及 N.I.N.A. 回差补偿会越过软限位；未移动。");
        return Enumerable.Range(-HalfPoints, HalfPoints * 2 + 1).Select(i => checked(origin + Step * i)).ToArray();
    }
}

public sealed record SepFocusState(int Position, string Binding, int BacklashIn, int BacklashOut);
public sealed record SepFocusSample(int Group, int Position, string Path, SepFocusFrame Measurement);
public sealed record SepFocusPoint(int Position, double R50, double R80, double Error, int Frames);
public sealed record SepFocusFit(int Position, double RSquared);
public sealed record SepMainFocusResult(string Status, int Origin, int? FinalPosition, int? Candidate,
    bool Improved, bool ReturnConfirmed, string Message, string Directory, SepFocusPoint[] Curve, SepFocusSample[] Samples);

/// <summary>Ports preserve physical ownership; only MoveAsync may move the bound focuser.</summary>
public interface ISepMainFocusHardware
{
    Task<SepFocusState> ReadReadyAsync(CancellationToken token);
    Task MoveAsync(int position, CancellationToken token);
    Task<SepFocusFrame> CaptureAsync(string newPath, SepFocusReference[]? reference, CancellationToken token);
    Task<SepFocusFit> FitAsync(SepFocusPoint[] points, CancellationToken token);
}

/// <summary>One shared runner for the visible preparation button and automation bridge.</summary>
public sealed class SepMainFocusRunner(ISepMainFocusHardware hardware)
{
    public static double Median(IEnumerable<double> values)
    {
        var a = values.Order().ToArray();
        if (a.Length == 0) throw new InvalidOperationException("没有共同星群测量。");
        return (a[(a.Length - 1) / 2] + a[a.Length / 2]) / 2;
    }

    public static SepFocusPoint[] Summarize(IReadOnlyList<SepFocusSample> samples)
    {
        var usable = samples.Where(s => s.Measurement.Stars.Length >= 3).ToArray();
        if (usable.Length == 0) throw new InvalidOperationException("没有可用的多星测量；保留原位。");
        var common = usable.Select(s => s.Measurement.Stars.Select(x => x.Id).ToHashSet())
            .Aggregate((a, b) => { a.IntersectWith(b); return a; });
        if (common.Count < 3) throw new InvalidOperationException("跨焦位的共同星群不足3颗；不把缺星记成零值。");
        return samples.GroupBy(s => s.Group).OrderBy(g => g.Key).Select(group =>
        {
            var frames = group.Where(s => common.All(id => s.Measurement.Stars.Any(x => x.Id == id)))
                .Select(s => s.Measurement.Stars.Where(x => common.Contains(x.Id)).ToArray()).ToArray();
            if (frames.Length < 2) throw new InvalidOperationException("某焦位不足2张共同星群有效帧；保留原位。");
            var r50 = frames.Select(f => Median(f.Select(s => s.R50))).ToArray();
            var median = Median(r50);
            return new SepFocusPoint(group.First().Position, median,
                Median(frames.Select(f => Median(f.Select(s => s.R80)))),
                Math.Max(.15, 1.4826 * Median(r50.Select(x => Math.Abs(x - median)))), frames.Length);
        }).ToArray();
    }

    public static bool AcceptRepeatedValidation(SepFocusPoint[] points) => points.Length == 4
        && new[] { 0, 2 }.All(i => points[i + 1].R50 <= .95 * points[i].R50 && points[i + 1].R80 <= 1.1 * points[i].R80);

    public async Task<SepMainFocusResult> RunAsync(SepMainFocusOptions options, string directory,
        IProgress<string>? progress, CancellationToken token)
    {
        var initial = await hardware.ReadReadyAsync(token);
        var positions = options.Positions(initial.Position, initial.BacklashIn, initial.BacklashOut);
        if (System.IO.Directory.Exists(directory)) throw new IOException("对焦证据目录已存在。");
        System.IO.Directory.CreateDirectory(directory);
        var samples = new List<SepFocusSample>();
        var curve = Array.Empty<SepFocusPoint>();
        int? candidate = null, final = null;
        var expected = initial.Position;
        var status = "Failed";
        var message = "";
        var improved = false;
        var confirmed = false;
        var group = 0;
        SepFocusReference[]? reference = null;
        var serial = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.MaximumSeconds));

        void Save(string name, object value)
        {
            using var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(true);
        }
        async Task Check(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var state = await hardware.ReadReadyAsync(ct);
            if (state.Binding != initial.Binding || state.Position != expected || state.BacklashIn != initial.BacklashIn || state.BacklashOut != initial.BacklashOut)
                throw new InvalidOperationException("主镜位置/设备/回差配置已被外部改变；不覆盖人工状态。");
        }
        async Task Move(int target, CancellationToken ct)
        {
            await Check(ct);
            while (expected != target)
            {
                var next = expected + Math.Clamp(target - expected, -150, 150);
                Save($"{++serial:0000}-move-intent.json", new { from = expected, target = next, utc = DateTimeOffset.UtcNow });
                expected = next; // A failed command is uncertain, never assumed not to have moved.
                await hardware.MoveAsync(next, ct);
                await Check(ct);
                Save($"{++serial:0000}-move-confirmed.json", new { position = next, utc = DateTimeOffset.UtcNow });
            }
        }
        async Task Sample(int position)
        {
            var ct = deadline.Token;
            await Move(position, ct);
            for (var frame = 0; frame < options.Frames; frame++)
            {
                await Check(ct);
                progress?.Report($"主镜 SEP 对焦：焦位 {position}，第 {frame + 1}/{options.Frames} 帧（PHD2 新帧）。");
                var path = Path.Combine(directory, $"{group:00}-focus-{position}-f{frame}.fit");
                var measurement = await hardware.CaptureAsync(path, reference, ct);
                await Check(ct);
                if (reference is null && measurement.Stars.Length >= 3) reference = measurement.Reference;
                var sample = new SepFocusSample(group, position, path, measurement);
                samples.Add(sample);
                Save($"{group:00}-focus-{position}-f{frame}.json", sample);
            }
            group++;
        }

        Save("plan.json", new { schema = 1, options, initial, positions, utc = DateTimeOffset.UtcNow,
            acceptance = "Same 3+ stars; 2+ frames; both A/B pairs improve R50 >=5%, R80 degradation <=10%", route = "NINA-SEP-main-focus" });
        try
        {
            await Sample(initial.Position);
            if (reference is null) throw new InvalidOperationException("基准帧未取得至少3颗参考星；未开始扫焦。");
            foreach (var position in positions) await Sample(position);
            curve = Summarize(samples);
            await Move(initial.Position, deadline.Token);
            var fit = await hardware.FitAsync(curve, deadline.Token);
            Save("native-fit.json", fit);
            if (!double.IsFinite(fit.RSquared) || fit.RSquared < .8 || fit.Position <= positions.Min() || fit.Position >= positions.Max())
                throw new InvalidOperationException("曲线没有可靠的区间内低谷；已回到起点，保留曲线供检查。");
            candidate = fit.Position;
            if (candidate != initial.Position)
            {
                var start = samples.Count;
                // New local star reference prevents long-sweep drift from becoming a fixed-coordinate veto.
                reference = null;
                foreach (var p in new[] { initial.Position, candidate.Value, initial.Position, candidate.Value }) await Sample(p);
                var validation = Summarize(samples.Skip(start).ToArray());
                improved = AcceptRepeatedValidation(validation);
                Save("validation.json", new { validation, improved });
            }
            await Move(improved ? candidate!.Value : initial.Position, deadline.Token);
            final = expected;
            confirmed = true;
            status = improved ? "Improved" : "Unchanged";
            message = improved ? $"往返验证通过，主镜保留在 {final}；下轮需重新建立 Night Setup 和入缝证据。"
                : $"未证明候选焦点有重复优势，主镜保留原位 {final}。这不是观测阻断。";
        }
        catch (Exception ex)
        {
            improved = false;
            status = ex is OperationCanceledException ? "Cancelled" : "Failed";
            message = ex.Message;
            try
            {
                using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await Move(initial.Position, restore.Token);
                final = initial.Position;
                confirmed = true;
            }
            catch (Exception recovery) { message += " 回位未确认：" + recovery.Message; }
        }
        var result = new SepMainFocusResult(status, initial.Position, final, candidate, improved, confirmed,
            message, directory, curve, samples.ToArray());
        Save("result.json", result);
        progress?.Report(message);
        return result;
    }
}
