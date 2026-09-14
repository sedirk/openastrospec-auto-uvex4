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

    public string? ConfigurationIssue =>
        MinimumPosition < 0 || MaximumPosition <= MinimumPosition ? "请填写主镜允许的最小、最大焦位；最大值必须大于最小值（当前尚未配置有效范围）。" :
        Step is < 1 or > 150 ? "相邻焦位间隔应为 1–150 步。" :
        HalfPoints is < 2 or > 4 ? "当前焦位两侧应各采样 2–4 处。" :
        Frames is < 2 or > 5 ? "每个焦位应拍摄 2–5 张图像。" :
        ExposureMilliseconds is < 500 or > 10000 ? "单张曝光应为 0.5–10 秒。" :
        GainPercent is < 0 or > 100 ? "导星相机增益应为 0–100%。" :
        !double.IsFinite(Saturation) || Saturation is <= 0 or > 65535 ? "请填写有效的传感器饱和值（1–65535 ADU）。" :
        !double.IsFinite(MinimumAltitude) || MinimumAltitude is < 0 or > 89 ? "最低目标高度应为 0–89 度。" :
        MaximumSeconds is < 60 or > 1800 ? "对焦总时限应为 60–1800 秒。" : null;

    public int[] Positions(int origin, int backlashIn, int backlashOut)
    {
        if (ConfigurationIssue is { } issue) throw new ArgumentException(issue);
        if (backlashIn < 0 || backlashOut < 0) throw new ArgumentException("N.I.N.A. 回差补偿量不能为负数。");
        var lower = (long)origin - (long)Step * HalfPoints - backlashIn;
        var upper = (long)origin + (long)Step * HalfPoints + backlashOut;
        if (lower < MinimumPosition || upper > MaximumPosition)
            throw new InvalidOperationException($"本次采样连同回差补偿需要 {lower}–{upper} 步，超出已设置的 {MinimumPosition}–{MaximumPosition} 步范围；未移动。");
        return Enumerable.Range(-HalfPoints, HalfPoints * 2 + 1).Select(i => checked(origin + Step * i)).ToArray();
    }
}

public sealed record SepFocusState(int Position, string Binding, int BacklashIn, int BacklashOut);
public sealed record SepFocusSample(int Group, int Position, string Path, SepFocusFrame Measurement);
public sealed record SepFocusPoint(int Position, double R50, double R80, double Error, int Frames);
public sealed record SepFocusFit(int Position, double RSquared);
public sealed record SepMainFocusResult(string Status, int Origin, int? FinalPosition, int? Candidate,
    bool Improved, bool ReturnConfirmed, string Message, string Directory, SepFocusPoint[] Curve, SepFocusSample[] Samples)
{
    public bool FocusVerified => ReturnConfirmed && FinalPosition is not null
        && (Status == "Improved" && Improved || Status == "VerifiedAtOrigin" && !Improved && FinalPosition == Origin);
}

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
        // Select ONE fixed cohort supported by at least two frames at EVERY
        // focus position. A third frame dropping one star must not veto both
        // complete frames. Selection uses identity/coverage, never focus value.
        if (samples.Count == 0) throw new InvalidOperationException("没有可用的多星测量；保留原位。");
        uint Mask(SepFocusSample sample) => sample.Measurement.Stars.Aggregate(0u, (mask, star) =>
            star.Id is >= 0 and < 30 ? mask | (1u << star.Id) : throw new InvalidDataException("Invalid focus star ID."));
        var candidates = new HashSet<uint> { (1u << 30) - 1 };
        foreach (var group in samples.GroupBy(s => s.Group).OrderBy(g => g.Key))
        {
            var frames = group.Select(Mask).ToArray();
            var pairs = new HashSet<uint>();
            for (var i = 0; i < frames.Length; i++)
                for (var j = i + 1; j < frames.Length; j++)
                    if (System.Numerics.BitOperations.PopCount(frames[i] & frames[j]) >= 3) pairs.Add(frames[i] & frames[j]);
            var intersections = candidates.SelectMany(c => pairs.Select(p => c & p))
                .Where(c => System.Numerics.BitOperations.PopCount(c) >= 3).Distinct().ToArray();
            if (intersections.Length == 0)
                throw new InvalidOperationException($"焦位 {group.First().Position} 未取得至少2帧、同一组至少3颗可比星；保留缺测，不补零。");
            if (intersections.Length > 10000) throw new InvalidOperationException("共同星群组合过多，未选择含糊的对焦曲线。");
            candidates = intersections.Where(c => !intersections.Any(other => other != c && (c & other) == c)).ToHashSet();
        }
        var selected = candidates.OrderByDescending(c => System.Numerics.BitOperations.PopCount(c))
            .ThenByDescending(c => samples.Count(s => (Mask(s) & c) == c)).ThenBy(c => c).First();
        var common = Enumerable.Range(0, 30).Where(i => (selected & (1u << i)) != 0).ToHashSet();
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

    public static bool AcceptVerifiedOrigin(SepFocusPoint[] points, int origin, int candidate, int step)
    {
        // A near-origin fitted minimum is a resolution-limited validation, not
        // a measured improvement. Include a fresh fifth group AFTER returning.
        if (points.Length != 5 || step < 1 || Math.Abs((long)candidate - origin) > step / 2d
            || !points.Select(p => p.Position).SequenceEqual(new[] { origin, candidate, origin, candidate, origin })
            || points.Any(p => p.Frames < 2 || !double.IsFinite(p.R50) || !double.IsFinite(p.R80) || p.R50 <= 0 || p.R80 < p.R50)) return false;
        // Verify the RETAINED position against both independent A measurements.
        // B is an alternative, not a repeat at A: a worse B must not veto A.
        // The fresh final A must also be non-inferior to BOTH B measurements;
        // no mean over good/bad positions, no tolerance change or zero filling.
        var final = points[4];
        static bool Agrees(double a, double b) => Math.Max(a, b) <= 1.1 * Math.Min(a, b);
        return new[] { 0, 2 }.All(i => Agrees(final.R50, points[i].R50) && Agrees(final.R80, points[i].R80))
            && new[] { 1, 3 }.All(i => final.R50 <= 1.1 * points[i].R50 && final.R80 <= 1.1 * points[i].R80);
    }

    public async Task<SepMainFocusResult> RunAsync(SepMainFocusOptions options, string directory,
        IProgress<string>? progress, CancellationToken token, IProgress<SepFocusPoint[]>? curveProgress = null)
    {
        // Bad form input must be rejected before even contacting a device owner.
        if (options.ConfigurationIssue is { } issue) throw new ArgumentException(issue);
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
        var verifiedOrigin = false;
        var confirmed = false;
        var group = 0;
        var samplingCurve = false;
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
            if (samplingCurve)
            {
                try { curve = Summarize(samples.Where(s => s.Group > 0).ToArray()); curveProgress?.Report(curve); }
                catch (InvalidOperationException) { /* Missing common-star data are not a zero-valued focus point. */ }
            }
        }

        Save("plan.json", new { schema = 1, options, initial, positions, utc = DateTimeOffset.UtcNow,
            acceptance = "Same 3+ stars; 2+ frames; both A/B pairs improve R50 >=5%, R80 degradation <=10%", route = "NINA-SEP-main-focus" });
        try
        {
            await Sample(initial.Position);
            if (reference is null) throw new InvalidOperationException("基准帧未取得至少3颗参考星；未开始扫焦。");
            samplingCurve = true;
            foreach (var position in positions) await Sample(position);
            curve = Summarize(samples.Where(s => s.Group > 0).ToArray());
            curveProgress?.Report(curve);
            await Move(initial.Position, deadline.Token);
            var fit = await hardware.FitAsync(curve, deadline.Token);
            Save("native-fit.json", fit);
            if (!double.IsFinite(fit.RSquared) || fit.RSquared < .8 || fit.Position <= positions.Min() || fit.Position >= positions.Max())
                throw new InvalidOperationException("曲线没有可靠的区间内低谷；已回到起点，保留曲线供检查。");
            candidate = fit.Position;
            samplingCurve = false;
            {
                var start = samples.Count;
                // New local star reference prevents long-sweep drift from becoming a fixed-coordinate veto.
                reference = null;
                foreach (var p in new[] { initial.Position, candidate.Value, initial.Position, candidate.Value }) await Sample(p);
                var validation = Summarize(samples.Skip(start).ToArray());
                improved = AcceptRepeatedValidation(validation);
                Save("validation.json", new { validation, improved });
                if (!improved && Math.Abs((long)candidate.Value - initial.Position) <= options.Step / 2d)
                {
                    await Sample(initial.Position);
                    var finalValidation = Summarize(samples.Skip(start).ToArray());
                    verifiedOrigin = AcceptVerifiedOrigin(finalValidation, initial.Position, candidate.Value, options.Step);
                    Save("origin-verification.json", new { finalValidation, verifiedOrigin,
                        meaning = "Reliable fitted minimum within half a sampling step; fresh final origin agrees with both earlier origin groups within 10% in R50/R80 and is no worse than either candidate group by >10%. No improvement claimed." });
                }
            }
            await Move(improved ? candidate!.Value : initial.Position, deadline.Token);
            final = expected;
            confirmed = true;
            status = improved ? "Improved" : verifiedOrigin ? "VerifiedAtOrigin" : "Unchanged";
            message = improved ? $"往返验证通过，主镜保留在 {final}；下轮需重新建立 Night Setup 和入缝证据。"
                : verifiedOrigin ? $"对焦验证通过：原位 {final} 已在本次最佳焦区内，回位新帧与往返测量一致；保留原位，不声称额外改善。"
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
