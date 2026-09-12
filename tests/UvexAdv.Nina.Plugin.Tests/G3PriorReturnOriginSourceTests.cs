using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3PriorReturnOriginSourceTests
{
    [Fact]
    public void OriginReadbackUsesOnlyOwnerReadsAndRetainsOldLedgerBeforeClosure()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources",
            "RealObservationStageRunner.G3PriorReturnOrigin.cs"));
        Assert.Contains("telescopeMediator.GetCurrentPosition()", source);
        Assert.Contains("ValidateImmediatePhysicalActionGates(context)", source);
        Assert.Contains("ValidateCommandCoordinateHorizon", source);
        Assert.Contains("phd2.Snapshot.AppState", source);
        Assert.Contains("G3AcquisitionMotionStore.LoadAsync", source);
        Assert.Contains("loaded.State != state", source);
        Assert.Contains("File.Copy(path", source);
        Assert.DoesNotContain("SlewToCoordinates", source);
        Assert.DoesNotContain("PulseGuide(", source);
        Assert.DoesNotContain("SetTracking", source);
        Assert.DoesNotContain("File.Delete", source);
        Assert.DoesNotContain("await PersistG3AcquisitionMotionAsync", source);
        Assert.True(source.IndexOf("PublishRunJsonEvidenceAsync", StringComparison.Ordinal) <
            source.IndexOf("G3AcquisitionMotionStore.WriteAtomicAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void PriorReturnProofRunsAfterIdentityAndInterlocksBeforeOldBudgetAdoption()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.cs"));
        var start = source.IndexOf("private async Task<StageResult?> RecoverDurableG3AcquisitionBeforeStageAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task<StageResult> RunG3WcsCenteringAsync", start, StringComparison.Ordinal);
        var recovery = source[start..end];
        var position = -1;
        foreach (var marker in new[] { "ValidateG3AcquisitionMotionIdentity(context, state)",
            "var recoveryInterlocks = await EvaluateInterlocksAsync", "G3PriorReturnOriginPolicy.IsCandidate",
            "return await VerifyPriorCrossPierReturnOriginAsync", "cumulativeCorrectionDegrees = Math.Max",
            "ReturnDurableG3AcquisitionToOriginAsync" })
        {
            var next = recovery.IndexOf(marker, position + 1, StringComparison.Ordinal);
            Assert.True(next > position, marker);
            position = next;
        }
    }
}
