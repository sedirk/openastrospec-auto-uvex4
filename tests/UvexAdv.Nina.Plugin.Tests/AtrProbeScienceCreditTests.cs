using UvexAdv.Nina.Plugin;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class AtrProbeScienceCreditTests
{
    private static GateResult Pass => GateResult.Pass("ok", "ok");
    [Fact] public void Final600SecondProbeCountsExactlyOnceIncludingAOneFramePlan()
    {
        var credit = new AtrProbeScienceCredit();
        Assert.True(credit.TryCredit("capture", "run", "run", 600, 600, Pass, Pass, true, 0, 1, 0, 1));
        Assert.False(credit.TryCredit("capture", "run", "run", 600, 600, Pass, Pass, true, 0, 1, 0, 1));
    }
    [Theory]
    [InlineData("tier")][InlineData("run")][InlineData("temperature")][InlineData("quality")]
    [InlineData("provenance")][InlineData("complete")][InlineData("budget")][InlineData("nan")]
    public void InvalidOrUnfundedProbeCannotBecomeScience(string reason)
    {
        Assert.False(new AtrProbeScienceCredit().TryCredit("capture", reason == "run" ? "old" : "run", "run",
            reason == "nan" ? double.NaN : 600, reason == "tier" ? 300 : 600,
            reason == "temperature" ? GateResult.Unknown("temperature", "") : Pass,
            reason == "quality" ? GateResult.Fail("GUIDING_UNSTABLE", "") : Pass,
            reason != "provenance", reason == "complete" ? 3 : 0, 3, reason == "budget" ? 6 : 0, 6));
    }
    [Fact] public void ExistingScienceWarningPolicyIsPreserved()
    {
        Assert.True(new AtrProbeScienceCredit().TryCredit("capture", "run", "run", 600, 600, Pass,
            GateResult.Warn("signal-limited", "stack"), true, 0, 3, 0, 6));
    }
}
