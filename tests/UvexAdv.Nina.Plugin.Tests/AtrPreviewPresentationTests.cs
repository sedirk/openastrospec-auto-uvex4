using System.Globalization;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrPreviewPresentationTests
{
    [Theory]
    [InlineData("zh-CN", "试拍", "Probe")]
    [InlineData("en-US", "Probe", "试拍")]
    public void ProbeCaptionUsesSelectedLanguageWithoutClaimingScienceAcceptance(string culture, string expected, string absent)
    {
        var caption = ObservationUiPresentation.AtrFrameCaption(false, 0, 0, 1, 60, 5904, 0, 114.7, CultureInfo.GetCultureInfo(culture));
        Assert.Contains(expected, caption);
        Assert.DoesNotContain(absent, caption);
        Assert.Contains("114.7", caption);
    }
}
