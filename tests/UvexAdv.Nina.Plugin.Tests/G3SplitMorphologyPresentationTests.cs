using System.Globalization;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public class G3SplitMorphologyPresentationTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public void SplitIsNotDiagnosedAsConfirmedBinaryOrCalibrationFailure(string culture)
    {
        var c = CultureInfo.GetCultureInfo(culture);
        var gate = GateResult.Unknown("G3_CATALOG_SHORT_POSITION_UNCONFIRMED",
            "G3_SEP_PARENT_BLEND_UNRESOLVED: Both whole parents remain split.",
            new Dictionary<string,double> { ["shortPositionFrames"]=5, ["maximumShortPositionFrames"]=5 });
        var p = ObservationUiPresentation.Present(ObservationStage.AcquireG3SlitField,gate,c);
        Assert.Equal(GateDisposition.Indeterminate,gate.Disposition);
        Assert.Contains("5/5",p.AutomaticRecovery);
        Assert.Contains("C11",p.Recommendation);
        Assert.Contains("M2",p.Recommendation);
        Assert.Contains("FITS",p.Recommendation);
        Assert.Contains("G3_SEP_PARENT_BLEND_UNRESOLVED",p.TechnicalDetails);
        if (culture == "zh-CN")
        {
            Assert.Contains("空心",p.Summary);
            Assert.Contains("不等于已确认双星",p.Summary);
            Assert.Contains("不会自动执行",p.Recommendation);
        }
        else
        {
            Assert.False(ObservationUiPresentation.ContainsCjk(p.Recommendation));
            Assert.Contains("No automatic focus or motion",p.Recommendation);
        }
    }

    [Fact]
    public void OrdinaryUnmeasuredFrameDoesNotGetDefocusAdvice()
    {
        var p = ObservationUiPresentation.Present(ObservationStage.AcquireG3SlitField,
            GateResult.Unknown("G3_CATALOG_SHORT_POSITION_UNCONFIRMED", "G3_SEP_UNMEASURED"),
            CultureInfo.GetCultureInfo("zh-CN"));
        Assert.DoesNotContain("空心",p.Summary);
        Assert.DoesNotContain("C11",p.Recommendation);
    }
}
