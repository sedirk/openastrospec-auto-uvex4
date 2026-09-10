using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using UvexAdv.Observatory;

namespace UvexAdv.Nina.Plugin;

[Export(typeof(IDockableVM))]
public sealed class PhotometryWorkerDockable : DockableVM, IDisposable
{
    private readonly IProfileService profiles;
    private readonly PhotometryWorkerHost host;
    private readonly ObservationCoordinatorHost observation;
    private readonly UvexPluginSettings settings;
    private string error = "";
    private NoticeKind notice;
    private string endpointDraft;
    private int selectedTabIndex;

    [ImportingConstructor]
    public PhotometryWorkerDockable(IProfileService profileService, PhotometryWorkerHost host,
        ObservationCoordinatorHost observation) : base(profileService)
    {
        profiles = profileService;
        this.host = host;
        this.observation = observation;
        settings = new UvexPluginSettings(profileService);
        endpointDraft = settings.QhyServiceUrl;
        Title = T("OpenAstroSpec 测光协同", "OpenAstroSpec Photometry");
        var icon = new GeometryGroup();
        icon.Children.Add(Geometry.Parse("M1,1 L15,1 15,15 1,15 Z M3,8 L6,8 8,3 10,12 12,8 14,8"));
        icon.Freeze();
        ImageGeometry = icon;
        BindCommand = new SimpleCommand(() => Run(host.BindSelectedDevices), () => IsPhotometryWorker && CanEditRole);
        EnableCommand = new SimpleCommand(() => Run(() => { host.Enable(); SelectedTabIndex = 1; }), () => IsPhotometryWorker && CanEditRole);
        PauseCommand = new SimpleAsyncCommand(() => RunAsync(host.PauseFromUiAsync),
            () => host.Enabled && !host.OperatorPaused && PhotometryUiPresentation.IsActive(host.LatestJob));
        StopCommand = new SimpleAsyncCommand(() => RunAsync(host.StopFromUiAsync),
            () => host.Enabled && (!host.OperatorPaused || PhotometryUiPresentation.IsActive(host.LatestJob)));
        AllowNewJobCommand = new SimpleCommand(() => Run(host.AllowNewJobFromUi),
            () => host.Enabled && host.OperatorPaused && !PhotometryUiPresentation.IsActive(host.LatestJob));
        CopyProfileIdCommand = new SimpleCommand(() => Copy(ProfileId));
        CopyWorkerEndpointCommand = new SimpleCommand(() => Copy(WorkerEndpoint), () => IsPhotometryWorker);
        SaveEndpointCommand = new SimpleCommand(() => Run(SaveEndpoint), () => IsSpectroscopyMaster && CanEditRole);
        host.Changed += Refresh;
        observation.DashboardChanged += OnDashboardChanged;
        profiles.ProfileChanged += OnProfileChanged;
        ObservationStaticTextLocalization.CultureChanged += OnCultureChanged;
    }

    public NinaInstanceRole Role
    {
        get => settings.InstanceRole;
        set
        {
            if (!CanEditRole) return;
            settings.InstanceRole = value;
            error = "";
            notice = NoticeKind.None;
            Refresh();
        }
    }
    public bool IsSpectroscopyMaster { get => Role == NinaInstanceRole.SpectroscopyMaster; set { if (value) Role = NinaInstanceRole.SpectroscopyMaster; } }
    public bool IsPhotometryWorker { get => Role == NinaInstanceRole.PhotometryWorker; set { if (value) Role = NinaInstanceRole.PhotometryWorker; } }
    public bool CanEditRole => !host.Enabled && observation.Dashboard.Run.State is
        ObservationRunState.Idle or ObservationRunState.Completed or ObservationRunState.Cancelled or ObservationRunState.Faulted;
    public int SelectedTabIndex { get => selectedTabIndex; set { selectedTabIndex = value; RaisePropertyChanged(); } }
    public string ProfileId => host.ProfileId;
    public string MasterProfileId
    {
        get => host.MasterProfileId;
        set { if (CanEditRole && IsPhotometryWorker) host.MasterProfileId = value; Refresh(); }
    }
    public string WorkerEndpoint => host.Endpoint;
    // Editing this text is a draft. Only Save applies it, while idle.
    public string MasterQhyEndpoint { get => endpointDraft; set { endpointDraft = value; RaisePropertyChanged(); } }
    public string EndpointSummary => PhotometryUiPresentation.EndpointSummary(settings.QhyServiceUrl, Culture);
    public string StatusTitle => IsSpectroscopyMaster
        ? T("本窗口：光谱主控", "This window: spectroscopy master")
        : !host.Enabled ? T("本窗口：测光端 · 尚未开始接收", "This window: photometry instance · reception not enabled")
        : host.OperatorPaused ? T("测光端：人工暂停/停止有效", "Photometry instance: operator pause/stop is active")
        : host.LatestJob is { } job ? PhotometryUiPresentation.JobState(job.State, Culture)
        : T("测光端：正在等待主控任务", "Photometry instance: waiting for a master job");
    public string NextAction => IsSpectroscopyMaster
        ? T("先完成两端配对，再回到“自动观测”设置同步测光开关并启动。地址已保存不代表另一实例已连接。", "Pair both instances, then use the simultaneous-photometry switch and Start in Automatic observation. A saved address does not prove a peer connection.")
        : host.Enabled ? PhotometryUiPresentation.JobAdvice(host.LatestJob, host.OperatorPaused, Culture)
        : !string.IsNullOrWhiteSpace(host.SetupIssue) ? PhotometryUiPresentation.Error(host.SetupIssue, Culture)
        : T("本地配置检查通过。点击“开始接收主控任务”；主控下发经过检查的任务之前不会连接设备或曝光。", "Local configuration checks passed. Enable reception; no equipment connects or exposes until the master submits a checked job.");
    public string DeviceSummary => string.Join("\n",
        DeviceBinding(T("测光相机", "Photometry camera"), settings.PhotometryCameraId),
        DeviceBinding(T("测光滤镜轮", "Photometry filter wheel"), settings.PhotometryFilterWheelId),
        DeviceBinding(T("测光电调焦", "Photometry focuser"), settings.PhotometryFocuserId));
    private string DeviceBinding(string role, string id) => role + " · " +
        (string.IsNullOrWhiteSpace(id) ? T("尚未记录", "not recorded") : T("已记录设备身份（不代表已连接）", "identity recorded (not proof of connection)"));
    public string JobSummary => PhotometryUiPresentation.JobSummary(host.LatestJob, Culture);
    public string JobAdvice => PhotometryUiPresentation.JobAdvice(host.LatestJob, host.OperatorPaused, Culture);
    public string Error => string.IsNullOrWhiteSpace(error) ? "" : PhotometryUiPresentation.Error(error, Culture);
    public string Notice => notice switch
    {
        NoticeKind.Copied => T("已复制。请粘贴到另一个 N.I.N.A. 窗口的对应步骤。", "Copied. Paste into the matching step in the other N.I.N.A. window."),
        NoticeKind.Saved => T("地址已保存到当前配置。没有连接设备；连接和身份检查将在真实流程启动时执行。", "Address saved to this Profile. No devices were connected; connection and identity checks run when the real workflow starts."),
        _ => "",
    };
    public string TechnicalDetails => string.Join("\n",
        error, host.Status, IsPhotometryWorker ? host.SetupIssue : "",
        $"ProfileId: {ProfileId}\nMasterProfileId: {MasterProfileId}\nEndpoint: {WorkerEndpoint}",
        $"Camera: {settings.PhotometryCameraId}\nFilterWheel: {settings.PhotometryFilterWheelId}\nFocuser: {settings.PhotometryFocuserId}",
        host.LatestJob is { } j ? $"{j.Kind} / {j.State}\n{j.Error}\n{j.AttentionReason}\n{j.ManifestPath}" : "");

    public ICommand BindCommand { get; }
    public ICommand EnableCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand AllowNewJobCommand { get; }
    public ICommand CopyProfileIdCommand { get; }
    public ICommand CopyWorkerEndpointCommand { get; }
    public ICommand SaveEndpointCommand { get; }

    private void SaveEndpoint()
    {
        var address = endpointDraft.Trim();
        if ((!NinaInstancePolicy.TryWorkerEndpoint(address, out var worker) && !PhotometryUiPresentation.IsLegacyEndpoint(address)) ||
            worker.ToString() == ProfileId)
            throw new InvalidOperationException("PHOTOMETRY_ENDPOINT_INVALID");
        settings.QhyServiceUrl = address;
        endpointDraft = address;
        notice = NoticeKind.Saved;
    }

    private void Copy(string value) => Run(() =>
    {
        Clipboard.SetText(value);
        notice = NoticeKind.Copied;
    });
    private void Run(Action action)
    {
        try { notice = NoticeKind.None; action(); error = ""; }
        catch (Exception ex) { error = ex.Message; }
        Refresh();
    }
    private async Task RunAsync(Func<Task> action)
    {
        try { notice = NoticeKind.None; await action(); error = ""; }
        catch (Exception ex) { error = ex.Message; }
        Refresh();
    }
    private void OnDashboardChanged(object? sender, ObservationDashboardSnapshot snapshot) => Refresh();
    private void OnCultureChanged(object? sender, EventArgs args) => Refresh();
    private void OnProfileChanged(object? sender, EventArgs args)
    {
        endpointDraft = settings.QhyServiceUrl;
        error = "";
        notice = NoticeKind.None;
        Refresh();
    }
    private void Refresh()
    {
        void Update()
        {
            Title = T("OpenAstroSpec 测光协同", "OpenAstroSpec Photometry");
            RaisePropertyChanged(string.Empty);
            foreach (var command in new[] { BindCommand, EnableCommand, PauseCommand, StopCommand,
                AllowNewJobCommand, CopyProfileIdCommand, CopyWorkerEndpointCommand, SaveEndpointCommand })
            {
                if (command is SimpleCommand sync) sync.RaiseCanExecuteChanged();
                else if (command is SimpleAsyncCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged();
            }
        }
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Update);
        else Update();
    }
    private static System.Globalization.CultureInfo Culture => ObservationStaticTextLocalization.EffectiveCulture;
    private enum NoticeKind { None, Copied, Saved }
    private static string T(string zh, string en) => ObservationUiPresentation.Text(zh, en, Culture);
    public void Dispose()
    {
        host.Changed -= Refresh;
        observation.DashboardChanged -= OnDashboardChanged;
        profiles.ProfileChanged -= OnProfileChanged;
        ObservationStaticTextLocalization.CultureChanged -= OnCultureChanged;
    }
}
