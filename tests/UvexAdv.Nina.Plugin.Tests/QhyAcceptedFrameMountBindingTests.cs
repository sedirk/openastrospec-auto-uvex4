using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class QhyAcceptedFrameMountBindingTests
{
    private static readonly DateTimeOffset Started = DateTimeOffset.Parse("2026-08-19T12:00:00Z");

    [Fact]
    public void DualEndedBindingAcceptsSameEpochPierAndBoundFrameWithinTwoArcseconds()
    {
        var binding = Create(afterRaDegrees: 10 + 1.5 / 3600d);

        var gate = binding.Validate(
            "run-a",
            new string('A', 64),
            new string('B', 64),
            binding.JobId,
            binding.FrameId,
            new string('C', 64),
            maximumSpanArcseconds: 2);

        Assert.Equal(GateDisposition.Passed, gate.Disposition);
        Assert.Equal("QHY_CAPTURE_MOUNT_BINDING_VALID", gate.Code);
    }

    [Fact]
    public void DualEndedBindingRejectsMountMotionAcrossExposure()
    {
        var binding = Create(afterRaDegrees: 10 + 2.2 / 3600d);

        var gate = binding.Validate(
            "run-a",
            new string('A', 64),
            new string('B', 64),
            binding.JobId,
            binding.FrameId,
            new string('C', 64),
            maximumSpanArcseconds: 2);

        Assert.Equal(GateDisposition.Indeterminate, gate.Disposition);
        Assert.Equal("QHY_CAPTURE_MOUNT_SPAN_EXCEEDED", gate.Code);
    }

    [Fact]
    public void DualEndedBindingRejectsTamperAndDifferentFrameHash()
    {
        var binding = Create(afterRaDegrees: 10);
        var tampered = binding with { AfterAcceptedFrame = binding.AfterAcceptedFrame with { DeclinationDegrees = 21 } };

        var tamperGate = tampered.Validate(
            "run-a",
            new string('A', 64),
            new string('B', 64),
            binding.JobId,
            binding.FrameId,
            new string('C', 64),
            2);
        var frameGate = binding.Validate(
            "run-a",
            new string('A', 64),
            new string('B', 64),
            binding.JobId,
            binding.FrameId,
            new string('D', 64),
            2);

        Assert.Equal("QHY_CAPTURE_MOUNT_BINDING_HASH_INVALID", tamperGate.Code);
        Assert.Equal("QHY_CAPTURE_MOUNT_BINDING_CONTEXT_CHANGED", frameGate.Code);
    }

    [Fact]
    public void DualEndedBindingRequiresReadbacksToBracketTheExposure()
    {
        var binding = Create(afterRaDegrees: 10);
        var lateBefore = binding with
        {
            BeforeJob = binding.BeforeJob with { ReportedUtc = binding.ExposureStartedUtc.AddMilliseconds(1) },
        };
        lateBefore = lateBefore with { BindingSha256 = lateBefore.ComputeBindingSha256() };
        var earlyAfter = binding with
        {
            AfterAcceptedFrame = binding.AfterAcceptedFrame with { ReportedUtc = binding.ExposureEndedUtc.AddMilliseconds(-1) },
        };
        earlyAfter = earlyAfter with { BindingSha256 = earlyAfter.ComputeBindingSha256() };

        Assert.Equal(
            "QHY_CAPTURE_MOUNT_BINDING_TIME_INVALID",
            lateBefore.Validate("run-a", new string('A', 64), new string('B', 64), binding.JobId, binding.FrameId, new string('C', 64), 2).Code);
        Assert.Equal(
            "QHY_CAPTURE_MOUNT_BINDING_TIME_INVALID",
            earlyAfter.Validate("run-a", new string('A', 64), new string('B', 64), binding.JobId, binding.FrameId, new string('C', 64), 2).Code);
    }

    private static QhyAcceptedFrameMountBinding Create(double afterRaDegrees)
    {
        var jobId = Guid.Parse("b5a7071b-3c20-46a6-b9f5-c3fce0c5c232");
        var frameId = Guid.Parse("fa8a66f6-b9ae-47a0-864f-e8896b5f0210");
        return QhyAcceptedFrameMountBinding.Create(
            "run-a",
            new string('A', 64),
            new string('B', 64),
            jobId,
            frameId,
            new string('C', 64),
            Started.AddSeconds(1),
            Started.AddSeconds(11),
            new G3FrameMountReadback(10, 20, "J2000", "pierEast", Started),
            new G3FrameMountReadback(afterRaDegrees, 20, "J2000", "pierEast", Started.AddSeconds(12)));
    }
}
