using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;

namespace UvexAdv.Nina.Plugin;

[Export(typeof(IDockableVM))]
public sealed class UvexDockable : DockableVM, IDisposable
{
    private readonly UvexPluginSettings settings;
    private readonly ICameraMediator cameraMediator;
    private readonly IImagingMediator imagingMediator;
    private readonly CancellationTokenSource lifetime = new();
    private string serviceStatus = "正在连接 UVEX4 控制服务…";
    private string cameraStatus = "尚未检查相机";
    private string error = string.Empty;

    [ImportingConstructor]
    public UvexDockable(IProfileService profileService, ICameraMediator cameraMediator, IImagingMediator imagingMediator)
        : base(profileService)
    {
        settings = new UvexPluginSettings(profileService);
        this.cameraMediator = cameraMediator;
        this.imagingMediator = imagingMediator;
        Title = "OpenAstroSpec 光谱";
        var icon = new GeometryGroup();
        icon.Children.Add(Geometry.Parse("M0,8 L3,8 5,2 8,14 11,5 14,8 16,8"));
        icon.Freeze();
        ImageGeometry = icon;
        BindCurrentCameraCommand = new SimpleAsyncCommand(BindCurrentCameraAsync);
        CaptureShadowCommand = new SimpleAsyncCommand(CaptureShadowAsync);
        UvexRuntimeState.Changed += OnRuntimeChanged;
        _ = PollAsync(lifetime.Token);
    }

    public string ServiceStatus { get => serviceStatus; private set { serviceStatus = value; RaisePropertyChanged(); } }
    public string CameraStatus { get => cameraStatus; private set { cameraStatus = value; RaisePropertyChanged(); } }
    public string Error { get => error; private set { error = value; RaisePropertyChanged(); } }
    public string MetricSummary => UvexRuntimeState.MetricSummary;
    public PointCollection SpectrumPoints => UvexRuntimeState.SpectrumPoints;
    public ICommand BindCurrentCameraCommand { get; }
    public ICommand CaptureShadowCommand { get; }

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
        UvexRuntimeState.Changed -= OnRuntimeChanged;
    }

    private Task BindCurrentCameraAsync()
    {
        var info = cameraMediator.GetInfo();
        if (!info.Connected || string.IsNullOrWhiteSpace(info.DeviceId))
        {
            throw new InvalidOperationException("请先在 N.I.N.A. 中连接 ATR585M。");
        }

        settings.BoundCameraId = info.DeviceId;
        CameraStatus = $"已绑定：{info.DisplayName ?? info.Name} · {info.DeviceId}";
        return Task.CompletedTask;
    }

    private async Task CaptureShadowAsync()
    {
        try
        {
            Error = string.Empty;
            var progress = new Progress<ApplicationStatus>();
            var capture = new NinaSpectrumCapture(cameraMediator, imagingMediator, settings, progress);
            _ = await capture.CaptureAsync(lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var client = new UvexServiceClient(settings.ServiceUrl);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ServiceStatus = status is null
                        ? "UVEX 服务无状态"
                        : $"{status.ConnectionState} · G {status.GratingPositionSteps?.ToString() ?? "?"} · M2 {status.FocusPositionSteps?.ToString() ?? "?"} · S {status.SlitPosition?.ToString() ?? "?"}";
                    var camera = cameraMediator.GetInfo();
                    CameraStatus = camera.Connected ? $"N.I.N.A. 相机：{camera.DisplayName ?? camera.Name}" : "N.I.N.A. 相机未连接";
                    Error = string.Empty;
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Application.Current.Dispatcher.Invoke(() => Error = ex.Message);
            }

            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnRuntimeChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            RaisePropertyChanged(nameof(MetricSummary));
            RaisePropertyChanged(nameof(SpectrumPoints));
        });
    }
}
