using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using UvexAdv.Core;

namespace UvexAdv.Admin;

internal sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(8);

    private readonly IUvexApiClient api;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim slitIlluminationGate = new(1, 1);
    private Task? pollingTask;
    private string? leaseToken;
    private DateTimeOffset? leaseExpiresUtc;
    private UvexDeviceStatus? status;
    private string gratingDeltaSteps = "100";
    private string focusDeltaSteps = "50";
    private string wavelengthNm = "550";
    private int slitPosition = 1;
    private string slitOffsetSteps = "0";
    private bool slitCalibrationConfirmed;
    private bool slitSelectionInitialized;
    private bool slitIlluminationBusy;
    private bool slitIlluminationCleanupRequired;
    private bool disposed;

    public MainViewModel(IUvexApiClient api)
    {
        this.api = api;
        AcquireLeaseCommand = Command(AcquireLeaseAsync, () => !HasValidControlLease);
        ReleaseLeaseCommand = Command(ReleaseLeaseAsync, () => leaseToken is not null);
        EmergencyStopCommand = Command(() => RunOperationAsync("/api/v1/device/stop", null, false));
        HomeGratingCommand = LeasedCommand(() => RunOperationAsync("/api/v1/grating/home", null, true));
        MoveGratingCommand = LeasedCommand(() => RunOperationAsync("/api/v1/grating/move", new { deltaSteps = ParseInt(GratingDeltaSteps) }, true));
        GotoWavelengthCommand = LeasedCommand(() => RunOperationAsync("/api/v1/grating/wavelength", new { wavelengthNm = ParseDouble(WavelengthNm) }, true));
        HomeFocusCommand = LeasedCommand(() => RunOperationAsync("/api/v1/focus/home", null, true));
        MoveFocusCommand = LeasedCommand(() => RunOperationAsync("/api/v1/focus/move", new { deltaSteps = ParseInt(FocusDeltaSteps) }, true));
        SelectSlitCommand = LeasedCommand(() => RunOperationAsync("/api/v1/slit/select", new { position = SlitPosition }, true));
        TurnOnSlitIlluminationCommand = Command(TurnOnSlitIlluminationAsync, CanOperateSlitIllumination);
        TurnOffSlitIlluminationCommand = Command(TurnOffSlitIlluminationAsync, CanOperateSlitIllumination);
        CalibrateSlitPositionCommand = Command(
            () => RunConfirmedCalibrationAsync("/api/v1/slit/calibrate-position", new { position = SlitPosition, confirmed = true }),
            () => HasValidControlLease && SlitCalibrationConfirmed);
        CalibrateSlitPhotodiodeCommand = Command(
            () => RunConfirmedCalibrationAsync("/api/v1/slit/calibrate-photodiode", new { confirmed = true }),
            () => HasValidControlLease && SlitCalibrationConfirmed);
        CalibrateSlitOffsetCommand = Command(
            () => RunConfirmedCalibrationAsync(
                "/api/v1/slit/calibrate-offset",
                new { position = SlitPosition, offsetSteps = ParseInt(SlitOffsetSteps), confirmed = true }),
            () => HasValidControlLease && SlitCalibrationConfirmed);
        EnterMaintenanceCommand = LeasedCommand(() => RunOperationAsync("/api/v1/device/maintenance/enter", null, true));
        ExitMaintenanceCommand = LeasedCommand(() => RunOperationAsync("/api/v1/device/maintenance/exit", null, true));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Messages { get; } = [];
    public IReadOnlyList<UvexSlitDefinition> SlitOptions => status?.Slits.Count > 0
        ? status.Slits
        : [new(1, "300um", 0), new(2, "15um", 0), new(3, "25um", 0), new(4, "35um", 0)];
    public ICommand AcquireLeaseCommand { get; }
    public ICommand ReleaseLeaseCommand { get; }
    public ICommand EmergencyStopCommand { get; }
    public ICommand HomeGratingCommand { get; }
    public ICommand MoveGratingCommand { get; }
    public ICommand GotoWavelengthCommand { get; }
    public ICommand HomeFocusCommand { get; }
    public ICommand MoveFocusCommand { get; }
    public ICommand SelectSlitCommand { get; }
    public ICommand TurnOnSlitIlluminationCommand { get; }
    public ICommand TurnOffSlitIlluminationCommand { get; }
    public ICommand CalibrateSlitPositionCommand { get; }
    public ICommand CalibrateSlitPhotodiodeCommand { get; }
    public ICommand CalibrateSlitOffsetCommand { get; }
    public ICommand EnterMaintenanceCommand { get; }
    public ICommand ExitMaintenanceCommand { get; }

    public string StatusSummary => status is null
        ? "服务未连接"
        : $"{status.ConnectionState} · {PositionTrustText} · {LeaseStateText}";
    public string DeviceDetails => status is null
        ? "http://127.0.0.1:47844"
        : $"{status.PortName} · FW {status.FirmwareVersion ?? "?"} · {status.Description ?? "UVEX4"} · {(status.TemperatureC.HasValue ? $"{status.TemperatureC:F1} °C" : "温度未知")}";
    public string ErrorDetails => status?.LastError is null ? string.Empty : $"连接错误：{status.LastError}";
    public string GratingStatus => $"{PositionPrefix}位置：{status?.GratingPositionSteps?.ToString() ?? "?"} steps · 中心：{status?.CentralWavelengthAngstrom?.ToString("F1") ?? "?"} Å";
    public string FocusStatus => $"{PositionPrefix}位置：{status?.FocusPositionSteps?.ToString() ?? "?"} steps";
    public string SlitStatus
    {
        get
        {
            var position = status?.SlitPosition;
            var name = position.HasValue ? status?.Slits.FirstOrDefault(slit => slit.Position == position)?.Name : null;
            return $"{PositionPrefix}当前狭缝：{(position.HasValue ? $"{position} - {name ?? "未命名"}" : "?")}";
        }
    }

    public string SlitCalibrationStatus =>
        $"轮电机：{status?.SlitMotorPositionSteps?.ToString() ?? "?"} steps · {(status?.SlitPhotodiodeEnabled == true ? "狭缝光电检测已启用" : "狭缝光电检测未启用/未知")}";
    public string SlitIlluminationStatus => SlitIlluminationPresentation.FormatCommandState(status);
    public string SlitPhotodiodeStatus => SlitIlluminationPresentation.FormatPhotodiode(status);
    public string SlitIlluminationWarning => SlitIlluminationPresentation.FormatWarning(status);

    public string GratingDeltaSteps { get => gratingDeltaSteps; set => Set(ref gratingDeltaSteps, value); }
    public string FocusDeltaSteps { get => focusDeltaSteps; set => Set(ref focusDeltaSteps, value); }
    public string WavelengthNm { get => wavelengthNm; set => Set(ref wavelengthNm, value); }
    public int SlitPosition
    {
        get => slitPosition;
        set
        {
            if (Set(ref slitPosition, value))
            {
                SlitOffsetSteps = status?.Slits.FirstOrDefault(slit => slit.Position == value)?.OffsetSteps?.ToString() ?? "0";
            }
        }
    }

    public string SlitOffsetSteps { get => slitOffsetSteps; set => Set(ref slitOffsetSteps, value); }
    public bool SlitCalibrationConfirmed
    {
        get => slitCalibrationConfirmed;
        set
        {
            if (Set(ref slitCalibrationConfirmed, value))
            {
                RaiseCommandState(CalibrateSlitPositionCommand);
                RaiseCommandState(CalibrateSlitPhotodiodeCommand);
                RaiseCommandState(CalibrateSlitOffsetCommand);
            }
        }
    }

    public Task StartAsync()
    {
        pollingTask ??= PollAsync(lifetime.Token);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetime.Cancel();

        if (pollingTask is not null)
        {
            try
            {
                await pollingTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AddMessage($"状态轮询结束异常：{ex.Message}");
            }
        }

        var token = leaseToken;
        if (token is not null)
        {
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            var offConfirmed = await BestEffortTurnOffAsync(token, "窗口关闭", cleanup.Token);
            try
            {
                await api.ReleaseLeaseAsync(token, cleanup.Token);
                leaseToken = null;
                leaseExpiresUtc = null;
                AddMessage(offConfirmed
                    ? "窗口关闭前已确认定位LED关灯并释放控制租约。"
                    : "已释放控制租约；定位LED关灯未确认，不能推定其实际状态。");
            }
            catch (Exception ex)
            {
                AddMessage($"窗口关闭时释放控制租约失败：{ex.Message}");
            }
        }

        api.Dispose();
        lifetime.Dispose();
    }

    internal void AddDiagnostic(string message) => AddMessage(message);

    private bool HasValidControlLease =>
        leaseToken is not null && leaseExpiresUtc is { } expires && expires > DateTimeOffset.UtcNow.AddSeconds(1);

    private string LeaseStateText => leaseToken is null
        ? "只读"
        : HasValidControlLease ? "已取得控制权" : "控制租约已失效";

    private bool CanOperateSlitIllumination() => HasValidControlLease && !slitIlluminationBusy;

    private AsyncCommand Command(Func<Task> execute, Func<bool>? canExecute = null) =>
        new(async () => await GuardAsync(execute), canExecute);

    private AsyncCommand LeasedCommand(Func<Task> execute) => Command(execute, () => HasValidControlLease);

    internal async Task AcquireLeaseAsync()
    {
        var lease = await api.AcquireLeaseAsync(lifetime.Token);
        leaseToken = lease.Token;
        leaseExpiresUtc = lease.ExpiresUtc;
        AddMessage($"取得控制租约，有效期至 {lease.ExpiresUtc.ToLocalTime():HH:mm:ss}");
        RaiseAll();
    }

    private async Task ReleaseLeaseAsync()
    {
        var token = leaseToken;
        if (token is null)
        {
            return;
        }

        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cleanup.CancelAfter(CleanupTimeout);
        var offConfirmed = await BestEffortTurnOffAsync(token, "释放控制租约", cleanup.Token);

        await api.ReleaseLeaseAsync(token, lifetime.Token);
        leaseToken = null;
        leaseExpiresUtc = null;
        AddMessage(offConfirmed
            ? "已确认定位LED关灯并释放控制租约。"
            : "已释放控制租约；定位LED关灯未确认，不能推定其实际状态。");
        RaiseAll();
    }

    private async Task<string> RequireFreshLeaseAsync(CancellationToken cancellationToken)
    {
        var token = leaseToken;
        if (token is null || leaseExpiresUtc is not { } expires || expires <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("控制操作需要有效且未过期的控制租约。");
        }

        if (expires - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(20))
        {
            var renewed = await api.RenewLeaseAsync(token, cancellationToken);
            leaseToken = renewed.Token;
            leaseExpiresUtc = renewed.ExpiresUtc;
            token = renewed.Token;
            RaiseAll();
        }

        return token;
    }

    internal Task TurnOnSlitIlluminationAsync() => SetSlitIlluminationAsync(true);

    internal Task TurnOffSlitIlluminationAsync() => SetSlitIlluminationAsync(false);

    private async Task SetSlitIlluminationAsync(bool enabled)
    {
        slitIlluminationBusy = true;
        RaiseIlluminationCommandState();
        await slitIlluminationGate.WaitAsync(lifetime.Token);
        try
        {
            var token = await RequireFreshLeaseAsync(lifetime.Token);
            if (enabled)
            {
                // Set this before sending: a lost HTTP response can leave the physical command outcome uncertain.
                slitIlluminationCleanupRequired = true;
            }

            using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            operationTimeout.CancelAfter(OperationTimeout);

            try
            {
                var operation = await SubmitAndWaitOperationAsync(
                    "/api/v1/slit/illumination",
                    new { enabled },
                    token,
                    operationTimeout.Token);
                EnsureOperationSucceeded(operation);
                await RefreshStatusAsync(operationTimeout.Token);

                var expected = enabled ? UvexOutputState.On : UvexOutputState.Off;
                if (status?.SlitIlluminationLedState != expected)
                {
                    throw new InvalidOperationException(
                        $"定位LED操作完成，但状态回读未确认“{(enabled ? "开" : "关")}”；不能推定实际状态。");
                }

                if (enabled)
                {
                    slitIlluminationCleanupRequired = true;
                    AddMessage("定位LED开灯命令已由服务确认；该状态是最近成功命令，不是硬件回读。");
                    var warning = SlitIlluminationPresentation.FormatWarning(status);
                    if (!string.IsNullOrEmpty(warning))
                    {
                        AddMessage(warning);
                    }
                }
                else
                {
                    slitIlluminationCleanupRequired = false;
                    AddMessage("定位LED关灯命令已由服务与状态字段确认。");
                }
            }
            catch
            {
                if (enabled)
                {
                    using var cleanup = new CancellationTokenSource(CleanupTimeout);
                    _ = await TryTurnOffCoreAsync(token, "开灯异常清理", cleanup.Token);
                }

                throw;
            }
        }
        finally
        {
            slitIlluminationGate.Release();
            slitIlluminationBusy = false;
            RaiseIlluminationCommandState();
        }
    }

    private async Task<bool> BestEffortTurnOffAsync(string token, string reason, CancellationToken cancellationToken)
    {
        try
        {
            token = await TryRenewLeaseForCleanupAsync(token, reason, cancellationToken);
            await slitIlluminationGate.WaitAsync(cancellationToken);
            try
            {
                return await TryTurnOffCoreAsync(token, reason, cancellationToken);
            }
            finally
            {
                slitIlluminationGate.Release();
            }
        }
        catch (Exception ex)
        {
            AddMessage($"警告：{reason}时定位LED关灯未确认：{ex.Message}；不能推定实际状态。");
            return false;
        }
    }

    private async Task<string> TryRenewLeaseForCleanupAsync(
        string token,
        string reason,
        CancellationToken cancellationToken)
    {
        if (leaseExpiresUtc is not { } expires || expires - DateTimeOffset.UtcNow >= TimeSpan.FromSeconds(10))
        {
            return token;
        }

        try
        {
            var renewed = await api.RenewLeaseAsync(token, cancellationToken);
            leaseToken = renewed.Token;
            leaseExpiresUtc = renewed.ExpiresUtc;
            return renewed.Token;
        }
        catch (Exception ex)
        {
            AddMessage($"警告：{reason}前控制租约续期失败：{ex.Message}；仍将尝试发送关灯命令。");
            return token;
        }
    }

    private async Task<bool> TryTurnOffCoreAsync(string token, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var operation = await SubmitAndWaitOperationAsync(
                "/api/v1/slit/illumination",
                new { enabled = false },
                token,
                cancellationToken);
            if (!operation.State.Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                AddMessage(
                    $"警告：{reason}的定位LED关灯操作为 {operation.State}{FormatOperationError(operation)}；不能推定实际状态。");
                return false;
            }

            await RefreshStatusAsync(cancellationToken);
            if (status?.SlitIlluminationLedState != UvexOutputState.Off)
            {
                AddMessage($"警告：{reason}的关灯操作已完成，但状态字段未确认关闭；不能推定实际状态。");
                return false;
            }

            slitIlluminationCleanupRequired = false;
            AddMessage($"{reason}：定位LED关灯命令已确认。");
            return true;
        }
        catch (Exception ex)
        {
            AddMessage($"警告：{reason}时定位LED关灯未确认：{ex.Message}；不能推定实际状态。");
            return false;
        }
    }

    private async Task RunConfirmedCalibrationAsync(string path, object body)
    {
        try
        {
            await RunOperationAsync(path, body, true);
        }
        finally
        {
            SlitCalibrationConfirmed = false;
        }
    }

    private async Task RunOperationAsync(string path, object? body, bool requiresLease)
    {
        var token = requiresLease ? await RequireFreshLeaseAsync(lifetime.Token) : null;
        var operation = await SubmitAndWaitOperationAsync(path, body, token, lifetime.Token);
        EnsureOperationSucceeded(operation);
    }

    private async Task<OperationResponse> SubmitAndWaitOperationAsync(
        string path,
        object? body,
        string? token,
        CancellationToken cancellationToken)
    {
        var operation = await api.PostOperationAsync(path, body, token, cancellationToken);
        AddMessage($"提交 {operation.Kind}：{operation.Id}");

        while (!IsTerminal(operation.State))
        {
            await Task.Delay(250, cancellationToken);
            operation = await api.GetOperationAsync(operation.Id, cancellationToken)
                ?? throw new InvalidOperationException($"服务未返回操作 {operation.Id} 的状态。");
        }

        AddMessage($"{operation.Kind} → {operation.State}{FormatOperationError(operation)}");
        return operation;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshStatusAsync(cancellationToken);
                if (leaseToken is not null && leaseExpiresUtc - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(20))
                {
                    try
                    {
                        var renewed = await api.RenewLeaseAsync(leaseToken, cancellationToken);
                        leaseToken = renewed.Token;
                        leaseExpiresUtc = renewed.ExpiresUtc;
                        RaiseAll();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        leaseExpiresUtc = DateTimeOffset.UtcNow;
                        AddMessage($"控制租约续期失败，控制按钮已禁用：{ex.Message}");
                        RaiseAll();
                        if (slitIlluminationCleanupRequired)
                        {
                            using var cleanup = new CancellationTokenSource(CleanupTimeout);
                            _ = await BestEffortTurnOffAsync(leaseToken, "租约续期异常清理", cleanup.Token);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AddMessage($"状态查询失败：{ex.Message}");
                if (leaseToken is not null && slitIlluminationCleanupRequired)
                {
                    using var cleanup = new CancellationTokenSource(CleanupTimeout);
                    _ = await BestEffortTurnOffAsync(leaseToken, "状态查询异常清理", cleanup.Token);
                }
            }

            try
            {
                await Task.Delay(2000, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        var refreshed = await api.GetStatusAsync(cancellationToken)
            ?? throw new InvalidOperationException("UVEX 服务返回了空状态。");
        ApplyStatus(refreshed);
    }

    private void ApplyStatus(UvexDeviceStatus refreshed)
    {
        status = refreshed;
        if (refreshed.SlitIlluminationLedState == UvexOutputState.On)
        {
            slitIlluminationCleanupRequired = true;
        }
        else if (refreshed.SlitIlluminationLedState == UvexOutputState.Off)
        {
            slitIlluminationCleanupRequired = false;
        }

        if (!slitSelectionInitialized && refreshed.SlitPosition is { } currentSlit)
        {
            slitSelectionInitialized = true;
            SlitPosition = currentSlit;
            SlitOffsetSteps = refreshed.Slits.FirstOrDefault(slit => slit.Position == currentSlit)?.OffsetSteps?.ToString() ?? "0";
        }

        RaiseAll();
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AddMessage($"错误：{ex.Message}");
            if (leaseToken is not null && slitIlluminationCleanupRequired)
            {
                using var cleanup = new CancellationTokenSource(CleanupTimeout);
                _ = await BestEffortTurnOffAsync(leaseToken, "异步操作异常清理", cleanup.Token);
            }
        }
    }

    private void AddMessage(string message)
    {
        Messages.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        while (Messages.Count > 200)
        {
            Messages.RemoveAt(Messages.Count - 1);
        }
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(StatusSummary));
        OnPropertyChanged(nameof(DeviceDetails));
        OnPropertyChanged(nameof(ErrorDetails));
        OnPropertyChanged(nameof(GratingStatus));
        OnPropertyChanged(nameof(FocusStatus));
        OnPropertyChanged(nameof(SlitStatus));
        OnPropertyChanged(nameof(SlitCalibrationStatus));
        OnPropertyChanged(nameof(SlitIlluminationStatus));
        OnPropertyChanged(nameof(SlitPhotodiodeStatus));
        OnPropertyChanged(nameof(SlitIlluminationWarning));
        OnPropertyChanged(nameof(SlitOptions));

        foreach (var command in new[]
                 {
                     AcquireLeaseCommand,
                     ReleaseLeaseCommand,
                     HomeGratingCommand,
                     MoveGratingCommand,
                     GotoWavelengthCommand,
                     HomeFocusCommand,
                     MoveFocusCommand,
                     SelectSlitCommand,
                     TurnOnSlitIlluminationCommand,
                     TurnOffSlitIlluminationCommand,
                     CalibrateSlitPositionCommand,
                     CalibrateSlitPhotodiodeCommand,
                     CalibrateSlitOffsetCommand,
                     EnterMaintenanceCommand,
                     ExitMaintenanceCommand,
                 })
        {
            RaiseCommandState(command);
        }
    }

    private void RaiseIlluminationCommandState()
    {
        RaiseCommandState(TurnOnSlitIlluminationCommand);
        RaiseCommandState(TurnOffSlitIlluminationCommand);
    }

    private static void RaiseCommandState(ICommand command) => (command as AsyncCommand)?.RaiseCanExecuteChanged();

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static int ParseInt(string value) => int.TryParse(value, out var parsed)
        ? parsed
        : throw new FormatException("请输入有效的整数步数。");

    private static double ParseDouble(string value) => double.TryParse(value, out var parsed)
        ? parsed
        : throw new FormatException("请输入有效的数值。");

    private static bool IsTerminal(string state) =>
        state is "Succeeded" or "Failed" or "Cancelled";

    private static void EnsureOperationSucceeded(OperationResponse operation)
    {
        if (!operation.State.Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{operation.Kind} 操作为 {operation.State}{FormatOperationError(operation)}");
        }
    }

    private static string FormatOperationError(OperationResponse operation) =>
        operation.Error is null ? string.Empty : $"：{operation.Error}";

    private string PositionTrustText => status?.PositionTrust switch
    {
        UvexPositionTrust.Live => "实时位置可信",
        UvexPositionTrust.LastKnown when status.PositionMeasuredUtc is { } measured => $"显示上次位置（{measured.ToLocalTime():MM-dd HH:mm:ss}）",
        UvexPositionTrust.LastKnown => "显示上次位置",
        _ => "尚无可信位置",
    };

    private string PositionPrefix => status?.PositionTrust == UvexPositionTrust.LastKnown ? "上次" : string.Empty;
}
