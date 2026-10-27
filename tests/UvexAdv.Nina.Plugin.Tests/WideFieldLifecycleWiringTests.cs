using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class WideFieldLifecycleWiringTests
{
    [Fact]
    public void StreamUsesBackgroundBoundaryAndLadderUsesAdaptiveExposureInProduction()
    {
        var calls = Calls(Body("AnalyzeWideFieldStreamFrameAsync")).Select(m=>m.Name).ToArray();
        Assert.Contains("RunWideFieldKeyframeWhenStationaryAsync",calls);
        Assert.DoesNotContain("RunWithG3PendingReturnTimeAsync",calls);
        Assert.Contains("WideFieldStreamCheckpointAsync",calls);
        Assert.Contains("ShortRecheckMilliseconds",Calls(Body("CaptureG3PlateSolveLadderAsync")).Select(m=>m.Name));
        Assert.Contains("WithoutContinuousOwnerAsync", Calls(Body("CaptureLocalDualWitnessAsync")).Select(m => m.Name));
        var centering = Calls(Body("RunG3WcsCenteringAsync")).Select(m => m.Name).ToArray();
        Assert.Contains("PlanCorrection", centering);
        Assert.Contains("ValidateOutboundAndReturnReserve", centering);
        Assert.Contains("ValidateG3FieldMountBindingForMotionAsync", centering);
    }

    [Theory]
    [InlineData("OnPausedAsync", "RetireWideFieldForBoundaryAsync")]
    [InlineData("OnTakeoverAsync", "RetireWideFieldForBoundaryAsync")]
    [InlineData("OnResumingAsync", "RequireWideFieldAcquisitionStoppedAsync")]
    [InlineData("StartOptionalQhyPhotometryAsync", "RequireWideFieldAcquisitionStoppedAsync")]
    [InlineData("StartQhyPhotometryAsync", "RequireWideFieldAcquisitionStoppedAsync")]
    [InlineData("FinalizeObservationAsync", "RetireWideFieldForBoundaryAsync")]
    [InlineData("CleanupAfterFailureCoreAsync", "RetireWideFieldForBoundaryAsync")]
    public void CompiledLifecycleRetiresTheRealStream(string entry, string expected)
    {
        Assert.Contains(expected, Calls(Body(entry)).Select(m => m.Name));
        Assert.Contains("StopWideFieldAcquisitionStreamAsync", Calls(Body(expected)).Select(m => m.Name));
        Assert.Contains(Calls(Body("StopWideFieldAcquisitionStreamAsync")),
            m => m.DeclaringType == typeof(QhyWideFieldStreamSession) && m.Name == "StopAsync");
    }

    [Fact]
    public void OnlyActualAcquisitionStartsWitnessAndBothSolverCallsUsePriorityOwner()
    {
        var acquisition = Calls(Body("AcquireG3SlitFieldAsync")).Select(m => m.Name).ToArray();
        Assert.Contains("EnsureWideFieldAcquisitionStreamAsync", acquisition);
        Assert.Contains("WideFieldAcquisitionDeadline", acquisition);
        var solve = Calls(Body("SolveImageAsync")).Select(m => m.Name).ToArray();
        Assert.Contains("RunWithImageSolverPriorityAsync", solve);
        // G3 wrapper's callback goes through the same helper; no direct solver
        // call remains in the main async method while a background one is active.
        Assert.DoesNotContain("Solve", solve);
    }

    private static MethodInfo Body(string name)
    {
        var method = typeof(RealObservationStageRunner).GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        return method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            .GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic) ?? method;
    }

    private static IEnumerable<MethodBase> Calls(MethodInfo method)
    {
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(c => (ushort)c.Value);
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        for (var i = 0; i < bytes.Length;)
        {
            ushort value = bytes[i++];
            if (value == 0xfe) value = (ushort)(0xfe00 | bytes[i++]);
            var op = codes[value]; var start = i;
            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, i),
                _ => 4,
            };
            if (op.OperandType == OperandType.InlineMethod &&
                method.Module.ResolveMethod(BitConverter.ToInt32(bytes, start)) is { } called)
                yield return called;
        }
    }
}
