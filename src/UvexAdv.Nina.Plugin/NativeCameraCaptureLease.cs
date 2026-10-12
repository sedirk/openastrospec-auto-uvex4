using NINA.Equipment.Interfaces.Mediator;

namespace UvexAdv.Nina.Plugin;

/// <summary>Borrow only the current native sequence's lock; never release it.</summary>
internal sealed class NativeCameraCaptureLease : IDisposable
{
    private readonly ICameraMediator camera;
    private readonly object owner;
    private readonly object foreignProbe = new();
    private readonly bool borrowed;
    private bool disposed;

    private NativeCameraCaptureLease(ICameraMediator camera, object owner, bool borrowed)
    {
        this.camera = camera; this.owner = owner; this.borrowed = borrowed;
    }

    internal static NativeCameraCaptureLease? TryAcquire(ICameraMediator camera, object? nativeOwner)
    {
        var localOwner = new object();
        if (camera.IsFreeToCapture(localOwner))
        {
            camera.RegisterCaptureBlock(localOwner);
            return new(camera, localOwner, false);
        }
        return nativeOwner is not null && camera.IsFreeToCapture(nativeOwner)
            ? new(camera, nativeOwner, true) : null;
    }

    internal bool IsCurrent(object? nativeOwner) => !disposed &&
        (!borrowed || ReferenceEquals(owner, nativeOwner)) &&
        camera.IsFreeToCapture(owner) && !camera.IsFreeToCapture(foreignProbe);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!borrowed) camera.ReleaseCaptureBlock(owner);
    }
}
