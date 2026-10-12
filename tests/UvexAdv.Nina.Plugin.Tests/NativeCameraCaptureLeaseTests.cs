using System.Reflection;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Interfaces.Mediator;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class NativeCameraCaptureLeaseTests
{
    [Fact]
    public void FactoryDefersSequenceEventsUntilNativeNavigationIsReady()
    {
        var sequences = DispatchProxy.Create<ISequenceMediator, Sequence>();
        var state = (Sequence)(object)sequences;
        using var factory = new RealObservationStageRunnerFactory(null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, sequences);
        Assert.Equal(0, state.Reads + state.Subscriptions);
        factory.ObserveNativeSequenceLifetime(); // Native getter before navigation registration throws.
        Assert.Equal(0, state.Subscriptions);
        state.Ready = false;
        factory.ObserveNativeSequenceLifetime();
        Assert.Equal(0, state.Subscriptions);
        state.Ready = true;
        factory.ObserveNativeSequenceLifetime();
        factory.ObserveNativeSequenceLifetime();
        Assert.Equal(2, state.Subscriptions);
        factory.Dispose();
        Assert.Equal(2, state.Removals);
    }

    public class Sequence : DispatchProxy
    {
        internal bool? Ready;
        internal int Reads, Subscriptions, Removals;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_Initialized")
            {
                Reads++;
                return Ready ?? throw new NullReferenceException("Native navigation not registered.");
            }
            if (method.Name.StartsWith("add_")) { Assert.True(Ready); Subscriptions++; return null; }
            if (method.Name.StartsWith("remove_")) { Removals++; return null; }
            throw new InvalidOperationException("Unexpected sequence action: " + method.Name);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalAndSequenceRecoveryRespectTheActualNativeCaptureOwner(bool sequence)
    {
        var owner = new object();
        var camera = DispatchProxy.Create<ICameraMediator, Camera>();
        var state = (Camera)(object)camera;
        if (sequence) state.Owner = owner;
        using (var lease = NativeCameraCaptureLease.TryAcquire(camera, sequence ? owner : null))
        {
            Assert.NotNull(lease);
            Assert.True(lease.IsCurrent(sequence ? owner : null));
            Assert.False(camera.IsFreeToCapture(new object()));
        }
        Assert.Equal(sequence ? owner : null, state.Owner);
        Assert.Equal(sequence ? 0 : 1, state.Registrations);
        Assert.Equal(sequence ? 0 : 1, state.Releases);
    }

    [Theory]
    [InlineData("external")]
    [InlineData("standalone")]
    public void ForeignOwnerCannotBeBorrowedOrReleased(string mode)
    {
        var camera = DispatchProxy.Create<ICameraMediator, Camera>();
        var state = (Camera)(object)camera;
        var foreign = state.Owner = new object();
        Assert.Null(NativeCameraCaptureLease.TryAcquire(camera, mode == "standalone" ? null : new object()));
        Assert.Same(foreign, state.Owner);
        Assert.Equal(0, state.Registrations + state.Releases);
    }

    [Theory]
    [InlineData("released")]
    [InlineData("replaced")]
    [InlineData("sequence-ended")]
    public void RecoveryCannotContinueAfterTheBorrowedLockBoundaryChanges(string change)
    {
        var camera = DispatchProxy.Create<ICameraMediator, Camera>();
        var state = (Camera)(object)camera;
        var owner = state.Owner = new object();
        using var lease = NativeCameraCaptureLease.TryAcquire(camera, owner);
        Assert.NotNull(lease);
        if (change == "released") state.Owner = null;
        if (change == "replaced") state.Owner = new object();
        Assert.False(lease.IsCurrent(change == "sequence-ended" ? null : owner));
        lease.Dispose();
        Assert.Equal(0, state.Releases);
    }

    public class Camera : DispatchProxy
    {
        internal object? Owner;
        internal int Registrations, Releases;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "IsFreeToCapture": return Owner is null || ReferenceEquals(Owner, args![0]);
                case "RegisterCaptureBlock":
                    Assert.Null(Owner); Owner = args![0]; Registrations++; return null;
                case "ReleaseCaptureBlock":
                    if (ReferenceEquals(Owner, args![0])) Owner = null;
                    Releases++; return null;
                default: throw new InvalidOperationException("Unexpected hardware operation: " + method.Name);
            }
        }
    }
}
