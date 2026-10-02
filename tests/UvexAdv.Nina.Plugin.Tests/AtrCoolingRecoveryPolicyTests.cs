using System.Globalization;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrCoolingRecoveryPolicyTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-02T14:49:38Z");
    private static GateResult Sample(double temp = 0, double target = -10) => AtrCoolingReadinessPolicy.Evaluate(
        new(true, "ATR", true, true, temp, target, 60), "ATR", target);

    [Fact]
    public void FrozenZeroGetsOneReapplyOneReconnectThenStopsWithoutTemperatureApproval()
    {
        var policy = new AtrCoolingRecoveryPolicy();
        Assert.Equal(AtrCoolingRecoveryAction.Wait, policy.Observe(Sample(), Start));
        Assert.Equal(AtrCoolingRecoveryAction.Wait, policy.Observe(Sample(), Start.AddSeconds(119)));
        Assert.Equal(AtrCoolingRecoveryAction.ReapplyTarget, policy.Observe(Sample(), Start.AddMinutes(2)));
        Assert.Equal(AtrCoolingRecoveryAction.ReconnectOwner, policy.Observe(Sample(), Start.AddMinutes(4)));
        Assert.Equal(AtrCoolingRecoveryAction.Exhausted, policy.Observe(Sample(), Start.AddMinutes(6)));
        Assert.NotEqual(GateDisposition.Passed, Sample().Disposition);
    }

    [Fact]
    public void ImprovingTemperatureDoesNotReconnectAndLegitimateZeroSetpointIsReady()
    {
        var policy = new AtrCoolingRecoveryPolicy();
        for (var i = 0; i < 20; i++)
            Assert.Equal(AtrCoolingRecoveryAction.Wait, policy.Observe(Sample(20 - i), Start.AddMinutes(i)));
        Assert.Equal(0, policy.ActionsUsed);
        Assert.Equal(AtrCoolingRecoveryAction.Wait, policy.Observe(Sample(0, 0), Start.AddHours(1)));
        Assert.Equal(GateDisposition.Passed, Sample(0, 0).Disposition);
    }

    [Theory]
    [InlineData("ATR_COOLING_IDENTITY_CHANGED")]
    [InlineData("ATR_COOLING_DISCONNECTED")]
    [InlineData("ATR_COOLING_FRESH_READ_FAILED")]
    public void OtherFailuresDoNotGrantReconnect(string code)
    {
        var policy = new AtrCoolingRecoveryPolicy();
        var bad = GateResult.Unknown(code, "not a stall");
        Assert.Equal(AtrCoolingRecoveryAction.Wait, policy.Observe(bad, Start));
        Assert.Equal(AtrCoolingRecoveryAction.Wait, policy.Observe(bad, Start.AddMinutes(5)));
        Assert.Equal(0, policy.ActionsUsed);
    }

    [Fact]
    public void RecoveryAllowanceIsNotReplenishedWhenTemperatureTemporarilyRecovers()
    {
        var policy = new AtrCoolingRecoveryPolicy();
        policy.Observe(Sample(), Start);
        policy.Observe(Sample(), Start.AddMinutes(2));
        policy.Observe(Sample(-10), Start.AddMinutes(3));
        policy.Observe(Sample(), Start.AddMinutes(4));
        Assert.Equal(AtrCoolingRecoveryAction.ReconnectOwner, policy.Observe(Sample(), Start.AddMinutes(6)));
    }

    [Fact]
    public void ChineseStatusShowsRealWaitingNotTheGenericUiNotice()
    {
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        var message = AtrCoolingRecoveryPolicy.DescribeWait(Sample(), TimeSpan.FromMinutes(7), 0, culture);
        var shown = ObservationUiPresentation.PresentUiNotice(message, culture).Message;
        Assert.Contains("曝光尚未开始", shown);
        Assert.Contains("0.0°C", shown);
        Assert.Contains("-10.0°C", shown);
        Assert.Contains("60%", shown);
        Assert.Contains("07:00", shown);
        Assert.DoesNotContain("界面操作状态已更新", shown);
    }
}
