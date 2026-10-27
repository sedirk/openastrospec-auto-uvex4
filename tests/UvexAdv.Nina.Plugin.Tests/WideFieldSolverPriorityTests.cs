using System.Reflection;
using System.Runtime.CompilerServices;
using NINA.PlateSolving;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class WideFieldSolverPriorityTests
{
    [Fact]
    public async Task ForegroundCancelsAndJoinsNativeBackgroundSolveBeforeUsingSameSolver()
    {
        var runner = Create();
        var started = Signal(); var cancellationSeen = Signal(); var cleanup = Signal(); var foregroundStarted = false;
        var active = 0;
        var background = Run(runner, "QHY/GS350 continuous formal keyframe", async token =>
        {
            Interlocked.Increment(ref active); started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                cancellationSeen.TrySetResult();
                await cleanup.Task; // Native cancellation is not native cleanup completion.
                Interlocked.Decrement(ref active);
            }
            return new() { Success = true };
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var foreground = Run(runner, "PHD2/G3 native solve", _ =>
        {
            foregroundStarted = true; Assert.Equal(0, Volatile.Read(ref active));
            return Task.FromResult(new PlateSolveResult { Success = true });
        });
        await cancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(foregroundStarted); Assert.False(foreground.IsCompleted);
        cleanup.TrySetResult();
        await Assert.ThrowsAsync<WideFieldKeyframePreemptedException>(() => background);
        Assert.True((await foreground).Success);
    }

    [Fact]
    public async Task BackgroundCannotSlipAheadOfAlreadyWaitingForegroundOrReleaseItsGate()
    {
        var runner = Create(); var owner = Signal(); var release = Signal(); var backgroundCalls = 0;
        var first = Run(runner, "initial QHY foreground", async _ =>
        { owner.TrySetResult(); await release.Task; return new() { Success = true }; });
        await owner.Task;
        using var cancelled = new CancellationTokenSource();
        var queued = Run(runner, "PHD2/G3 queued", _ => Task.FromResult(new PlateSolveResult()), cancelled.Token);
        await Assert.ThrowsAsync<WideFieldKeyframePreemptedException>(() => Run(runner, "QHY/GS350 continuous formal keyframe", _ =>
        { Interlocked.Increment(ref backgroundCalls); return Task.FromResult(new PlateSolveResult()); }));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await Assert.ThrowsAsync<WideFieldKeyframePreemptedException>(() => Run(runner, "QHY/GS350 continuous formal keyframe", _ =>
        { Interlocked.Increment(ref backgroundCalls); return Task.FromResult(new PlateSolveResult()); }));
        Assert.Equal(0, backgroundCalls);
        release.TrySetResult(); await first;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static RealObservationStageRunner Create()
    {
        // No constructor: this finite test opens no clients or physical devices.
        var runner = (RealObservationStageRunner)RuntimeHelpers.GetUninitializedObject(typeof(RealObservationStageRunner));
        typeof(RealObservationStageRunner).GetField("imageSolverPriorityGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runner, new SemaphoreSlim(1, 1));
        typeof(RealObservationStageRunner).GetField("imageSolverPriorityStateGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runner, new object());
        return runner;
    }
    private static Task<PlateSolveResult> Run(RealObservationStageRunner runner, string role,
        Func<CancellationToken, Task<PlateSolveResult>> solve, CancellationToken token = default) =>
        (Task<PlateSolveResult>)typeof(RealObservationStageRunner).GetMethod("RunWithImageSolverPriorityAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(PlateSolveResult)).Invoke(runner, [role, solve, token])!;
}
