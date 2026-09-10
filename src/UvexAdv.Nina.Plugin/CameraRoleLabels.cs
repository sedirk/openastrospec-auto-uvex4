using System.Globalization;
using System.Text.RegularExpressions;

namespace UvexAdv.Nina.Plugin;

/// <summary>
/// Maps historical station shorthand in operator prose to camera roles.
/// Never apply to device identity fields, protocol data, paths or raw evidence.
/// </summary>
internal static partial class CameraRoleLabels
{
    internal static string Description(string text, CultureInfo? culture = null)
    {
        var zh = ObservationUiPresentation.IsChinese(culture);
        var spectrum = zh ? "光谱相机" : "spectroscopy camera";
        var photometry = zh ? "测光相机" : "photometry camera";
        var guide = zh ? "光谱仪导星相机" : "spectrograph guide camera";
        text = text.Replace("QHY/GS350", "QHY", StringComparison.Ordinal)
            .Replace("GS350/QHY", "QHY", StringComparison.Ordinal)
            .Replace("GS350 / QHY", "QHY", StringComparison.Ordinal);
        text = CameraToken().Replace(text, match => match.Value switch
        {
            "ATR" or "ATR585M" => spectrum,
            "QHY" or "QHYminiCam8M" => photometry,
            _ => guide,
        });
        return zh ? ChineseGap().Replace(text, "") : text;
    }

    [GeneratedRegex("(?<![A-Za-z0-9_])(?:ATR585M|ATR|QHYminiCam8M|QHY|G3M2210M|G3)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant)]
    private static partial Regex CameraToken();

    [GeneratedRegex("(?<=[\\u3400-\\u9fff]) +(?=[\\u3400-\\u9fff])", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseGap();
}
