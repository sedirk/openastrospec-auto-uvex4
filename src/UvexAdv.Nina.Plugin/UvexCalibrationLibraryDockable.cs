using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using UvexAdv.Core;

namespace UvexAdv.Nina.Plugin;

[Export(typeof(IDockableVM))]
public sealed class UvexCalibrationLibraryDockable : DockableVM, IDisposable
{
    private static readonly JsonSerializerOptions JobJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly UvexPluginSettings settings;
    private readonly IProfileService profileServiceRef;
    private readonly ICameraMediator cameraMediator;
    private readonly IImagingMediator imagingMediator;
    private readonly IImageDataFactory imageDataFactory;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? runCancellation;
    private string libraryRoot;
    private int gain;
    private int offset;
    private short binning;
    private double temperatureC;
    private double temperatureToleranceC;
    private double biasExposureSeconds;
    private int warmupFrameCount;
    private int biasFrameCount;
    private string darkExposureSecondsCsv;
    private int darkFrameCountEach;
    private bool buildMasters;
    private bool darknessConfirmed;
    private bool isRunning;
    private string cameraStatus = "尚未检查相机";
    private string runStatus = "空闲";
    private string inventoryStatus = "尚未扫描校准库";
    private string lastSavedPath = string.Empty;
    private string error = string.Empty;
    private double progressPercent;

    [ImportingConstructor]
    public UvexCalibrationLibraryDockable(
        IProfileService profileService,
        ICameraMediator cameraMediator,
        IImagingMediator imagingMediator,
        IImageDataFactory imageDataFactory)
        : base(profileService)
    {
        profileServiceRef = profileService;
        this.cameraMediator = cameraMediator;
        this.imagingMediator = imagingMediator;
        this.imageDataFactory = imageDataFactory;
        settings = new UvexPluginSettings(profileService);
        libraryRoot = settings.CalibrationLibraryRoot;
        gain = settings.CalibrationGain;
        offset = settings.CalibrationOffset;
        binning = settings.CalibrationBinning;
        temperatureC = settings.CalibrationTemperatureC;
        temperatureToleranceC = settings.CalibrationTemperatureToleranceC;
        biasExposureSeconds = settings.BiasExposureSeconds;
        warmupFrameCount = settings.CalibrationWarmupFrameCount;
        biasFrameCount = settings.BiasFrameCount;
        darkExposureSecondsCsv = settings.DarkExposureSecondsCsv;
        darkFrameCountEach = settings.DarkFrameCountEach;
        buildMasters = settings.BuildCalibrationMasters;

        Title = "OpenAstroSpec 校准库";
        var icon = new GeometryGroup();
        icon.Children.Add(Geometry.Parse("M1,2 L15,2 15,14 1,14 Z M3,5 L13,5 M3,8 L13,8 M3,11 L9,11"));
        icon.Freeze();
        ImageGeometry = icon;

        StartCommand = new SimpleAsyncCommand(StartFromUiAsync);
        CancelCommand = new SimpleCommand(Cancel, () => IsRunning);
        RefreshCommand = new SimpleCommand(RefreshInventory);
        OpenLibraryCommand = new SimpleCommand(OpenLibrary);
        BindCurrentCameraCommand = new SimpleCommand(BindCurrentCamera);
        _ = PollAsync(lifetime.Token);
    }

    public string LibraryRoot
    {
        get => libraryRoot;
        set { libraryRoot = value; settings.CalibrationLibraryRoot = value; RaisePropertyChanged(); }
    }
    public int Gain { get => gain; set { gain = value; settings.CalibrationGain = value; RaisePropertyChanged(); } }
    public int Offset { get => offset; set { offset = value; settings.CalibrationOffset = value; RaisePropertyChanged(); } }
    public short Binning { get => binning; set { binning = value; settings.CalibrationBinning = value; RaisePropertyChanged(); } }
    public double TemperatureC { get => temperatureC; set { temperatureC = value; settings.CalibrationTemperatureC = value; RaisePropertyChanged(); } }
    public double TemperatureToleranceC { get => temperatureToleranceC; set { temperatureToleranceC = value; settings.CalibrationTemperatureToleranceC = value; RaisePropertyChanged(); } }
    public double BiasExposureSeconds { get => biasExposureSeconds; set { biasExposureSeconds = value; settings.BiasExposureSeconds = value; RaisePropertyChanged(); } }
    public int WarmupFrameCount { get => warmupFrameCount; set { warmupFrameCount = value; settings.CalibrationWarmupFrameCount = value; RaisePropertyChanged(); } }
    public int BiasFrameCount { get => biasFrameCount; set { biasFrameCount = value; settings.BiasFrameCount = value; RaisePropertyChanged(); } }
    public string DarkExposureSecondsCsv { get => darkExposureSecondsCsv; set { darkExposureSecondsCsv = value; settings.DarkExposureSecondsCsv = value; RaisePropertyChanged(); } }
    public int DarkFrameCountEach { get => darkFrameCountEach; set { darkFrameCountEach = value; settings.DarkFrameCountEach = value; RaisePropertyChanged(); } }
    public bool BuildMasters { get => buildMasters; set { buildMasters = value; settings.BuildCalibrationMasters = value; RaisePropertyChanged(); } }
    public bool DarknessConfirmed { get => darknessConfirmed; set { darknessConfirmed = value; RaisePropertyChanged(); } }
    public bool IsRunning
    {
        get => isRunning;
        private set
        {
            isRunning = value;
            RaisePropertyChanged();
            (CancelCommand as SimpleCommand)?.RaiseCanExecuteChanged();
        }
    }
    public string CameraStatus { get => cameraStatus; private set { cameraStatus = value; RaisePropertyChanged(); } }
    public string RunStatus { get => runStatus; private set { runStatus = value; RaisePropertyChanged(); } }
    public string InventoryStatus { get => inventoryStatus; private set { inventoryStatus = value; RaisePropertyChanged(); } }
    public string LastSavedPath { get => lastSavedPath; private set { lastSavedPath = value; RaisePropertyChanged(); } }
    public string Error { get => error; private set { error = value; RaisePropertyChanged(); } }
    public double ProgressPercent { get => progressPercent; private set { progressPercent = value; RaisePropertyChanged(); } }
    public string BoundCameraId => string.IsNullOrWhiteSpace(settings.BoundCameraId) ? "未绑定" : settings.BoundCameraId;
    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenLibraryCommand { get; }
    public ICommand BindCurrentCameraCommand { get; }

    public void Dispose()
    {
        runCancellation?.Cancel();
        runCancellation?.Dispose();
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private async Task StartFromUiAsync()
    {
        if (!DarknessConfirmed)
        {
            Error = "请先确认镜头盖和屋顶均已关闭。ATR585M 没有机械快门。";
            return;
        }

        await RunCaptureAsync(null, lifetime.Token).ConfigureAwait(true);
    }

    private async Task RunCaptureAsync(string? claimedJobPath, CancellationToken cancellationToken)
    {
        if (IsRunning) return;
        IsRunning = true;
        Error = string.Empty;
        ProgressPercent = 0;
        runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var plan = CreatePlan();
            var capture = new NinaCalibrationLibraryCapture(
                profileServiceRef,
                cameraMediator,
                imagingMediator,
                imageDataFactory,
                new Progress<ApplicationStatus>());
            var result = await capture.CaptureAsync(
                plan,
                LibraryRoot,
                BuildMasters,
                new Progress<CalibrationCaptureProgress>(OnCaptureProgress),
                runCancellation.Token).ConfigureAwait(true);
            RunStatus = $"完成：{result.RawFrameCount} 张原始帧，{result.MasterCount} 个 master";
            LastSavedPath = result.ConfigurationDirectory;
            if (result.Warnings.Count > 0) Error = string.Join("；", result.Warnings);
            CompleteClaimedJob(claimedJobPath, "complete", null);
            RefreshInventory();
        }
        catch (OperationCanceledException)
        {
            RunStatus = "已安全取消；已保存的帧仍保留在库中";
            CompleteClaimedJob(claimedJobPath, "cancelled", null);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            RunStatus = "失败";
            CompleteClaimedJob(claimedJobPath, "failed", ex.Message);
        }
        finally
        {
            runCancellation.Dispose();
            runCancellation = null;
            DarknessConfirmed = false;
            IsRunning = false;
        }
    }

    private CalibrationCapturePlan CreatePlan()
    {
        var info = cameraMediator.GetInfo();
        if (!info.Connected) throw new InvalidOperationException("请先在 N.I.N.A. 的相机页连接 ATR585M。");
        if (string.IsNullOrWhiteSpace(settings.BoundCameraId))
        {
            throw new InvalidOperationException("尚未绑定相机；请点击“绑定当前 ATR585M”。");
        }

        if (!string.Equals(info.DeviceId, settings.BoundCameraId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("当前相机不是已绑定设备；不会按相机列表顺序猜测。");
        }

        var darkExposures = ParseExposureList(DarkExposureSecondsCsv);
        var actualBiasExposure = info.ExposureMin > 0
            ? Math.Max(BiasExposureSeconds, info.ExposureMin)
            : BiasExposureSeconds;
        if (actualBiasExposure != BiasExposureSeconds)
        {
            BiasExposureSeconds = actualBiasExposure;
            RunStatus = $"Bias 曝光已提升到相机最短曝光 {actualBiasExposure:G6} s";
        }

        return CalibrationCapturePlan.Create(
            info.DisplayName ?? info.Name ?? "ATR585M",
            settings.BoundCameraId,
            Gain,
            Offset,
            Binning,
            info.ReadoutMode,
            info.ReadoutModes?.ElementAtOrDefault(info.ReadoutMode) ?? $"Mode {info.ReadoutMode}",
            TemperatureC,
            TemperatureToleranceC,
            WarmupFrameCount,
            actualBiasExposure,
            BiasFrameCount,
            darkExposures,
            DarkFrameCountEach);
    }

    private static IReadOnlyList<double> ParseExposureList(string value)
    {
        var output = new List<double>();
        foreach (var token in value.Replace('，', ',').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var exposure) || exposure <= 0)
            {
                throw new FormatException($"无法解析暗场曝光“{token}”；请用英文逗号分隔秒数，例如 300,600。");
            }

            output.Add(exposure);
        }

        return output;
    }

    private void OnCaptureProgress(CalibrationCaptureProgress value)
    {
        ProgressPercent = value.Percent;
        RunStatus = value.Message;
        if (!string.IsNullOrWhiteSpace(value.LastSavedPath)) LastSavedPath = value.LastSavedPath;
    }

    private void Cancel() => runCancellation?.Cancel();

    private void BindCurrentCamera()
    {
        try
        {
            var info = cameraMediator.GetInfo();
            if (!info.Connected || string.IsNullOrWhiteSpace(info.DeviceId))
            {
                throw new InvalidOperationException("请先在 N.I.N.A. 中连接 ATR585M。");
            }

            var identity = string.Join('|', info.Name, info.DisplayName, info.Description, info.DeviceId);
            if (!identity.Contains(settings.ExpectedCameraName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"当前设备“{info.DisplayName ?? info.Name}”不是配置的 {settings.ExpectedCameraName}。");
            }

            settings.BoundCameraId = info.DeviceId;
            RaisePropertyChanged(nameof(BoundCameraId));
            Error = string.Empty;
            CameraStatus = CameraDescription(info);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private void RefreshInventory()
    {
        try
        {
            if (!Directory.Exists(LibraryRoot))
            {
                InventoryStatus = "库目录尚未创建";
                return;
            }

            var files = Directory.EnumerateFiles(LibraryRoot, "*.fits", SearchOption.AllDirectories).ToArray();
            var masters = files.Count(path => path.Contains($"{Path.DirectorySeparatorChar}masters{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
            var sessions = Directory.EnumerateFiles(LibraryRoot, "session-*.json", SearchOption.AllDirectories).Count();
            var bytes = files.Sum(path => new FileInfo(path).Length);
            InventoryStatus = $"{files.Length - masters} 张原始帧 · {masters} 个 master · {sessions} 次会话 · {bytes / 1024d / 1024d / 1024d:0.00} GiB";
        }
        catch (Exception ex)
        {
            InventoryStatus = $"扫描失败：{ex.Message}";
        }
    }

    private void OpenLibrary()
    {
        try
        {
            Directory.CreateDirectory(LibraryRoot);
            Process.Start(new ProcessStartInfo { FileName = LibraryRoot, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        RefreshInventory();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var info = cameraMediator.GetInfo();
                Application.Current.Dispatcher.Invoke(() => CameraStatus = CameraDescription(info));
                if (!IsRunning && info.Connected)
                {
                    var claimedJob = TryClaimPendingJob(info.DeviceId);
                    if (claimedJob is not null)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(async () =>
                        {
                            ApplyJob(claimedJob.Value.Job);
                            await RunCaptureAsync(claimedJob.Value.Path, cancellationToken).ConfigureAwait(true);
                        }).Task.Unwrap().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Application.Current.Dispatcher.Invoke(() => Error = ex.Message);
            }

            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        }
    }

    private (CalibrationStartupJob Job, string Path)? TryClaimPendingJob(string? connectedCameraId)
    {
        var directory = JobDirectory();
        var pendingPath = Path.Combine(directory, "pending.json");
        if (!File.Exists(pendingPath)) return null;
        Directory.CreateDirectory(directory);
        var job = JsonSerializer.Deserialize<CalibrationStartupJob>(File.ReadAllText(pendingPath), JobJsonOptions)
            ?? throw new InvalidDataException("pending.json 不是有效的校准库任务。");
        if (job.SchemaVersion != 1 || !job.DarknessConfirmed)
        {
            throw new InvalidDataException("无人值守校准任务缺少有效版本或暗环境确认。");
        }
        if (job.ExpiresUtc < DateTimeOffset.UtcNow)
        {
            CompletePendingJob(pendingPath, "expired", "任务已过期。");
            return null;
        }
        if (!string.Equals(job.CameraId, connectedCameraId, StringComparison.Ordinal)) return null;

        var claimedPath = Path.Combine(directory, $"running-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json");
        File.Move(pendingPath, claimedPath);
        return (job, claimedPath);
    }

    private void ApplyJob(CalibrationStartupJob job)
    {
        if (string.IsNullOrWhiteSpace(settings.BoundCameraId))
        {
            settings.BoundCameraId = job.CameraId;
            RaisePropertyChanged(nameof(BoundCameraId));
        }
        else if (!string.Equals(settings.BoundCameraId, job.CameraId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("无人值守任务的相机 DeviceId 与插件现有绑定不一致。");
        }

        LibraryRoot = job.LibraryRoot;
        Gain = job.Gain;
        Offset = job.Offset;
        Binning = job.Binning;
        TemperatureC = job.TemperatureC;
        TemperatureToleranceC = job.TemperatureToleranceC;
        BiasExposureSeconds = job.BiasExposureSeconds;
        WarmupFrameCount = job.WarmupFrameCount;
        BiasFrameCount = job.BiasFrameCount;
        DarkExposureSecondsCsv = string.Join(',', job.DarkExposureSeconds.Select(value => value.ToString(CultureInfo.InvariantCulture)));
        DarkFrameCountEach = job.DarkFrameCountEach;
        BuildMasters = job.BuildMasters;
        DarknessConfirmed = true;
        RunStatus = "已接收经确认的无人值守校准库任务";
    }

    private static void CompleteClaimedJob(string? claimedJobPath, string status, string? error)
    {
        if (string.IsNullOrWhiteSpace(claimedJobPath) || !File.Exists(claimedJobPath)) return;
        CompletePendingJob(claimedJobPath, status, error);
    }

    private static void CompletePendingJob(string path, string status, string? error)
    {
        var resultPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $"{status}-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json");
        var content = File.ReadAllText(path);
        var envelope = new { status, completedUtc = DateTimeOffset.UtcNow, error, request = JsonSerializer.Deserialize<JsonElement>(content) };
        File.WriteAllText(resultPath, JsonSerializer.Serialize(envelope, JobJsonOptions));
        File.Delete(path);
    }

    private static string JobDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UVEX-ADV",
        "calibration-jobs");

    private static string CameraDescription(NINA.Equipment.Equipment.MyCamera.CameraInfo info)
    {
        if (!info.Connected) return "N.I.N.A. 相机未连接";
        var readoutName = info.ReadoutModes?.ElementAtOrDefault(info.ReadoutMode) ?? "未知";
        return $"{info.DisplayName ?? info.Name} · {info.XSize}×{info.YSize} · {info.Temperature:0.0} °C / {info.TemperatureSetPoint:0.0} °C · " +
               $"读出 R{info.ReadoutMode} {readoutName} · 制冷 {(info.CoolerOn ? "开" : "关")} · {(info.IsExposing ? "曝光中" : "空闲")} · 无快门确认={(info.HasShutter ? "否" : "是")}";
    }

    private sealed class CalibrationStartupJob
    {
        public int SchemaVersion { get; init; } = 1;
        public DateTimeOffset CreatedUtc { get; init; }
        public DateTimeOffset ExpiresUtc { get; init; }
        public bool DarknessConfirmed { get; init; }
        public string CameraId { get; init; } = string.Empty;
        public string LibraryRoot { get; init; } = string.Empty;
        public int Gain { get; init; }
        public int Offset { get; init; }
        public short Binning { get; init; }
        public double TemperatureC { get; init; }
        public double TemperatureToleranceC { get; init; }
        public double BiasExposureSeconds { get; init; }
        public int WarmupFrameCount { get; init; } = 2;
        public int BiasFrameCount { get; init; }
        public double[] DarkExposureSeconds { get; init; } = [];
        public int DarkFrameCountEach { get; init; }
        public bool BuildMasters { get; init; }
    }
}
