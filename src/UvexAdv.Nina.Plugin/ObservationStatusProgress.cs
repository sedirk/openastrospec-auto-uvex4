using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NINA.Core.Model;
using NINA.Core.Utility;

namespace UvexAdv.Nina.Plugin;

/// <summary>Compact only the native status bar; runner messages and evidence remain unchanged.</summary>
internal sealed class ObservationStatusProgress(IProgress<ApplicationStatus> destination) : IProgress<ApplicationStatus>
{
    internal const string Source = "OpenAstroSpec";
    private string? lastDetail;

    internal static IProgress<ApplicationStatus> Wrap(IProgress<ApplicationStatus> progress) =>
        progress is ObservationStatusProgress ? progress : new ObservationStatusProgress(progress);

    public void Report(ApplicationStatus value)
    {
        if (value.Source?.StartsWith(Source, StringComparison.Ordinal) != true)
        {
            destination.Report(value); // Native camera/mount progress keeps its owner and semantics.
            return;
        }
        var detail = $"{value.Source}: {value.Status}";
        if (detail != lastDetail && !string.IsNullOrEmpty(value.Status))
        {
            Logger.Debug(detail);
            lastDetail = detail;
        }
        var label = Compact(value.Status);
        if (!string.IsNullOrEmpty(label) && (value.Source.Contains("模拟", StringComparison.Ordinal) ||
            value.Source.Contains("simulation", StringComparison.OrdinalIgnoreCase))) label = "SIM · " + label;
        // One owner entry, not a separate permanent entry for startup, stages,
        // real-run diagnostics and closeout. Do not mutate the original report.
        destination.Report(new ApplicationStatus
        {
            Source = Source, Status = label, Progress = value.Progress, MaxProgress = value.MaxProgress,
            ProgressType = value.ProgressType, Status2 = Compact(value.Status2), Progress2 = value.Progress2,
            MaxProgress2 = value.MaxProgress2, ProgressType2 = value.ProgressType2,
            Status3 = Compact(value.Status3), Progress3 = value.Progress3,
            MaxProgress3 = value.MaxProgress3, ProgressType3 = value.ProgressType3,
        });
    }

    internal static string Compact(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        var residual = Regex.Match(message, @"(?:ATR|光谱相机).*?警告[:：].*?残差 (?<values>[\d., ]+) px");
        if (residual.Success)
        {
            var values = residual.Groups["values"].Value.Split(',', StringSplitOptions.TrimEntries)
                .Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN)
                .Where(double.IsFinite).ToArray();
            if (values.Length > 0) return $"入缝偏差 ≤{values.Max().ToString("F2", CultureInfo.InvariantCulture)} px · 警告试拍";
        }
        var text = Regex.Replace(message.Trim(), @"\s+", " ");
        var clause = text.IndexOfAny(['；', ';', '\n']);
        if (clause > 0) text = text[..clause];
        var result = new StringBuilder();
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += rune.IsAscii ? 1 : 2;
            if (width > 52) return result.Append('…').ToString();
            result.Append(rune);
        }
        return text;
    }
}
