using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

// Automatic-only owner adapter. The manual focus runner and its curve/ABAB
// estimator stay shared. The night token remains authoritative even when that
// runner uses a fresh token to try a safe return after an ordinary fit failure.
internal sealed class AutomaticSepFocusBoundary(
    ISepMainFocusHardware inner, FocusMotionLimits limits, SepMainFocusOptions options,
    Func<CancellationToken, Task> checkSafety,
    Func<CancellationToken, Task<IReadOnlyList<string>>> confirmStopped,
    Func<CancellationToken, Task<bool>> reserveScan,
    DateTimeOffset hardDeadline, CancellationToken nightToken) : ISepMainFocusHardware
{
    internal const int ReturnReserveSeconds = 65;
    private SepFocusState? initial;
    private int expected;
    private long charged;
    internal bool ScanReserved { get; private set; }
    internal long ChargedSteps => charged;
    internal Exception? BoundaryFailure { get; private set; }
    internal Task? UnjoinedOperation { get; private set; }

    // Called only after the real owners have joined and an independent physical
    // stop/readback has succeeded. A local acquisition deadline is not a night
    // cancellation; neither permits a new return move after expiry.
    internal static GateResult? ClassifyOutcome(SepMainFocusResult? result, GateResult? failure,
        int origin, int finalPosition, bool positionConfirmed, bool originalDeadlineExpired,
        bool cancellationRequested, GateResult? nightStop)
    {
        if (nightStop is not null) return nightStop;
        if (!positionConfirmed)
            return GateResult.Unknown("AUTO_FOCUS_POSITION_UNCONFIRMED", "自动对焦最终焦位与停止状态未确认，不继续下一目标。");
        if (originalDeadlineExpired && finalPosition == origin
            && (failure is null || failure.Code == "AUTO_FOCUS_CANCELLED"))
            return GateResult.TargetQuality("AUTO_FOCUS_BUDGET_EXHAUSTED_RETURNED", "原目标/采集预算到达；已确认原焦位与物理停止，不追加回位，按原失败策略跳过目标。");
        if (failure?.Code == "AUTO_FOCUS_MEASUREMENT_UNAVAILABLE" && finalPosition == origin)
            return GateResult.TargetQuality("AUTO_FOCUS_MEASUREMENT_UNAVAILABLE_RETURNED", "可选 SEP 焦质测量不可用；已确认原所有者停止、原焦位保留，按原目标失败策略处理。");
        if (failure is not null) return failure;
        var verifiedPosition = result is { FocusVerified: true, ReturnConfirmed: true }
            && result.FinalPosition == finalPosition;
        if (originalDeadlineExpired && verifiedPosition)
            return GateResult.TargetQuality("AUTO_FOCUS_BUDGET_EXHAUSTED_VERIFIED", "焦质验证与保留焦位已确认，但原目标预算已到；保留合法新焦位，跳过本目标，不重发预算或强制回位。");
        if (cancellationRequested)
            return GateResult.Unknown("AUTO_FOCUS_CANCELLED", "观测已取消，不重新入场。");
        if (result is null) return null;
        if (verifiedPosition && !originalDeadlineExpired)
            return GateResult.Pass("AUTO_FOCUS_VERIFIED", "共享主镜对焦与重复验证通过；旧光学段退休，仅允许在原预算内新建运行段重新取场/入缝/导星。");
        if (finalPosition == origin)
            return GateResult.TargetQuality("AUTO_FOCUS_NOT_IMPROVED_RETURNED", "未验证焦质改善，已确认保留原焦位并停止；按原目标失败策略处理。");
        return GateResult.Unknown("AUTO_FOCUS_ORIGIN_UNCONFIRMED", "未改善且未确认回到原焦位，不能当作普通质量失败跳过。");
    }

    internal static SepMainFocusOptions? Constrain(SepMainFocusOptions saved, FocusMotionLimits limits)
    {
        if (saved.ConfigurationIssue is not null || limits.MaximumSingleMoveSteps <= 0
            || limits.MaximumCumulativeMoveSteps < limits.MaximumSingleMoveSteps
            || limits.MinimumPositionSteps < 0 || limits.MaximumPositionSteps <= limits.MinimumPositionSteps)
            return null;
        var result = saved with { MinimumPosition = Math.Max(saved.MinimumPosition, limits.MinimumPositionSteps),
            MaximumPosition = Math.Min(saved.MaximumPosition, limits.MaximumPositionSteps) };
        return result.ConfigurationIssue is null ? result : null;
    }

    // Precharge enough commissioned travel for the complete saved scan and
    // worst-case candidate/origin verification, including native overshoots.
    internal static long RequiredTravel(SepMainFocusOptions options, SepFocusState state, FocusMotionLimits limits)
    {
        var positions = options.Positions(state.Position, state.BacklashIn, state.BacklashOut);
        var step = Math.Min(150, limits.MaximumSingleMoveSteps - 2 * Math.Max(state.BacklashIn, state.BacklashOut));
        if (step < 1) return long.MaxValue;
        var radius = Math.Max(Math.Abs((long)positions[0] - state.Position), Math.Abs((long)positions[^1] - state.Position));
        var segments = 6 * ((radius + step - 1) / step)
            + 2L * options.HalfPoints * ((options.Step + step - 1L) / step);
        return 8 * radius + segments * 2 * Math.Max(state.BacklashIn, state.BacklashOut);
    }

    public async Task<SepFocusState> ReadReadyAsync(CancellationToken token)
    {
        SepFocusState state;
        try { state = await GuardAsync(ct => inner.ReadReadyAsync(ct), token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { BoundaryFailure ??= ex; throw; }
        if (initial is null) { initial = state; expected = state.Position; }
        else if (state.Binding != initial.Binding || state.BacklashIn != initial.BacklashIn || state.BacklashOut != initial.BacklashOut)
        {
            var issue = new InvalidOperationException("自动对焦所有者或原生回差配置改变。");
            BoundaryFailure ??= issue;
            throw issue;
        }
        return state;
    }

    public async Task MoveAsync(int position, CancellationToken token)
    {
        var state = await ReadReadyAsync(token).ConfigureAwait(false);
        if (state.Position != expected) throw new InvalidOperationException("自动对焦当前位置被外部改变，未覆盖人工状态。");
        var origin = initial!.Position;
        var maxBacklash = Math.Max(state.BacklashIn, state.BacklashOut);
        var stepLimit = Math.Min(150, limits.MaximumSingleMoveSteps - 2 * maxBacklash);
        if (stepLimit <= 0 || maxBacklash > limits.BacklashCompensationSteps)
            throw new InvalidOperationException("原生回差超出已标定单段/回差限额，未移动。");
        if ((long)position - state.BacklashIn < options.MinimumPosition
            || (long)position + state.BacklashOut > options.MaximumPosition)
            throw new InvalidOperationException("自动对焦位置连同回差补偿超出已保存/已标定交集。");
        while (expected != position)
        {
            var next = expected + Math.Clamp(position - expected, -stepLimit, stepLimit);
            var cost = Math.Abs((long)next - expected) + 2L * maxBacklash;
            var returnDistance = Math.Abs((long)next - origin);
            var returnCost = returnDistance + ((returnDistance + stepLimit - 1) / stepLimit) * 2L * maxBacklash;
            if (charged + cost + returnCost > limits.MaximumCumulativeMoveSteps)
                throw new InvalidOperationException("已用焦移加本段及原位回程超出原标定累计限额；未追加动作。");
            if (!ScanReserved)
            {
                await CheckAsync(token).ConfigureAwait(false);
                if (!await reserveScan(token).ConfigureAwait(false))
                    throw new InvalidOperationException("整夜未批准或已用过自动扫焦额度，未发起焦移。");
                ScanReserved = true;
            }
            charged += cost; // Charge BEFORE dispatch; an uncertain command is never refunded.
            expected = next;
            await GuardAsync(async ct => { await inner.MoveAsync(next, ct).ConfigureAwait(false); return true; }, token).ConfigureAwait(false);
            state = await ReadReadyAsync(token).ConfigureAwait(false);
            if (state.Position != next) throw new InvalidOperationException("自动焦移实际位置未到达请求值。");
        }
    }

    public Task<SepFocusFrame> CaptureAsync(string path, SepFocusReference[]? reference, CancellationToken token) =>
        GuardAsync(ct => inner.CaptureAsync(path, reference, ct), token);
    public Task<SepFocusFit> FitAsync(SepFocusPoint[] points, CancellationToken token) =>
        GuardAsync(ct => inner.FitAsync(points, ct), token);

    private async Task CheckAsync(CancellationToken token)
    {
        if (UnjoinedOperation is not null)
            throw new InvalidOperationException("自动对焦上一操作尚未结清，禁止新的设备操作或回位。");
        nightToken.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= hardDeadline) throw new OperationCanceledException("原目标/采集/整夜时间预算已到。", token);
        try { await checkSafety(token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { BoundaryFailure ??= ex; throw; }
    }

    private async Task<T> GuardAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        await CheckAsync(token).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, nightToken);
        var remaining = hardDeadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) throw new OperationCanceledException("原恢复时限已到。", linked.Token);
        linked.CancelAfter(remaining);
        var operation = action(linked.Token);
        try
        {
            while (!operation.IsCompleted)
            {
                await Task.WhenAny(operation, Task.Delay(250, linked.Token)).ConfigureAwait(false);
                await CheckAsync(linked.Token).ConfigureAwait(false);
            }
            var result = await operation.ConfigureAwait(false);
            await CheckAsync(linked.Token).ConfigureAwait(false);
            return result;
        }
        catch
        {
            linked.Cancel();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            IReadOnlyList<string> errors;
            try { errors = await confirmStopped(stop.Token).ConfigureAwait(false); }
            catch (Exception ex) { errors = [ex.Message]; }
            // Stop commands do not grant permission to detach an in-flight task.
            if (!await NinaSepMainFocusHardware.TryJoinOwnerOperationAsync(operation, TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false))
            {
                UnjoinedOperation = operation;
                var issue = new InvalidOperationException("自动对焦完整设备/测量操作未结清；禁止新目标、释放所有者或重建空闲证明。");
                BoundaryFailure ??= issue;
                throw issue;
            }
            if (errors.Count > 0)
            {
                var issue = new InvalidOperationException("自动对焦停止边界未确认：" + string.Join("; ", errors));
                BoundaryFailure ??= issue;
                throw issue;
            }
            throw;
        }
    }
}
