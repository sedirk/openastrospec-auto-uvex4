using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Runtime.Versioning;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using Newtonsoft.Json;

namespace UvexAdv.Nina.Plugin.SequenceItems;

[SupportedOSPlatform("windows")]
internal static class SequenceStartupScope
{
    internal static SequenceRootContainer? Root(ISequenceItem item) =>
        item.Parent is StartAreaContainer start && start.Parent is SequenceRootContainer root &&
        ReferenceEquals(root.Items.FirstOrDefault(), start) ? root : null;

    internal static bool HasRealPreparation(UvexTargetObservationContainer target)
    {
        if (target.Parent is not UvexNightSequenceContainer night || night.Parent is not TargetAreaContainer area ||
            area.Parent is not SequenceRootContainer root || root.Items.FirstOrDefault() is not StartAreaContainer start)
            return false;
        return start.Items.OfType<UvexSelectRealModeItem>().Any(x => x.EnableRealMode && x.Validate());
    }

    internal static void ValidateRealTargets(ISequenceItem item)
    {
        var root = Root(item) ?? throw new InvalidOperationException("启动卡片须放在根序列的开始区域，不能放进目标、循环或并行容器。");
        var targets = root.Items.OfType<TargetAreaContainer>().SelectMany(x => x.Items)
            .OfType<UvexNightSequenceContainer>().SelectMany(x => x.Items).OfType<UvexTargetObservationContainer>().ToArray();
        if (targets.Length == 0 || targets.Any(x => !x.UseRealMode))
            throw new InvalidOperationException("启用启动卡片前，请将本序列的每张光谱目标卡明确设为 REAL；不会自动把模拟目标改成真实。");
    }
}

[ExportMetadata("Name", "OpenAstroSpec · 选择真实观测模式")]
[ExportMetadata("Description", "放在开始区域。明确启用后选择真实模式；不启动目标、不授权后台控制、不绕过安全或调试检查。")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
[SupportedOSPlatform("windows")]
public sealed class UvexSelectRealModeItem : SequenceItem, IValidatable
{
    private readonly IProfileService profiles;
    private readonly ObservationCoordinatorHost host;
    private readonly UvexPluginSettings settings;
    [ImportingConstructor]
    public UvexSelectRealModeItem(IProfileService profiles, ObservationCoordinatorHost host)
        : this(profiles, host, new UvexPluginSettings(profiles)) { }
    internal UvexSelectRealModeItem(IProfileService profiles, ObservationCoordinatorHost host, UvexPluginSettings settings)
    { this.profiles = profiles; this.host = host; this.settings = settings; }

    [JsonProperty] public bool EnableRealMode { get; set; }
    [JsonIgnore] public IList<string> Issues { get; } = new ObservableCollection<string>();
    public bool Validate()
    {
        Issues.Clear();
        if (EnableRealMode)
            try { NinaInstancePolicy.RequireMaster(settings); SequenceStartupScope.ValidateRealTargets(this);
                if (!settings.RealModeCommissioned) throw new InvalidOperationException("本 Profile 尚未完成真实设备调试确认。"); }
            catch (InvalidOperationException ex) { Issues.Add(ex.Message); }
        return Issues.Count == 0;
    }
    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!EnableRealMode) return;
        if (!Validate()) throw new InvalidOperationException(string.Join(" ", Issues));
        // Same exclusive gate as observation/focus; cannot hot-switch a running night.
        using var reservation = await host.ReserveNightAsync(this, true, token);
        token.ThrowIfCancellationRequested();
        settings.ObservationUseRealMode = true;
        ObservationStatusProgress.Wrap(progress).Report(new ApplicationStatus { Source = "OpenAstroSpec 启动", Status = "真实模式已选择 · 待安全检查" });
    }
    public override object Clone()
    {
        var clone = new UvexSelectRealModeItem(profiles, host, settings) { EnableRealMode = EnableRealMode };
        clone.CopyMetaData(this); return clone;
    }
}

[ExportMetadata("Name", "OpenAstroSpec · 自动启动安全监视器（旧序列兼容）")]
[ExportMetadata("Description", "通过 N.I.N.A. 连接 Profile 已选定的安全监视器。已连接不重复启动；连接不等于安全，后面仍须等待安全。")]
[ExportMetadata("Category", "OpenAstroSpec Auto / 兼容旧序列")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
[SupportedOSPlatform("windows")]
public sealed class UvexStartSafetyMonitorItem : SequenceItem, IValidatable
{
    private readonly IProfileService profiles;
    private readonly ObservationCoordinatorHost host;
    private readonly ISafetyMonitorMediator monitor;
    private readonly UvexPluginSettings settings;
    private static bool IsSelected(string? id) => !string.IsNullOrWhiteSpace(id) && id != "No_Device";
    [ImportingConstructor]
    public UvexStartSafetyMonitorItem(IProfileService profiles, ObservationCoordinatorHost host, ISafetyMonitorMediator monitor)
        : this(profiles, host, monitor, new UvexPluginSettings(profiles)) { }
    internal UvexStartSafetyMonitorItem(IProfileService profiles, ObservationCoordinatorHost host,
        ISafetyMonitorMediator monitor, UvexPluginSettings settings)
    { this.profiles = profiles; this.host = host; this.monitor = monitor; this.settings = settings; }

    [JsonProperty] public bool StartMonitoring { get; set; }
    [JsonIgnore] public IList<string> Issues { get; } = new ObservableCollection<string>();
    public bool Validate()
    {
        Issues.Clear();
        if (StartMonitoring)
            try { NinaInstancePolicy.RequireMaster(settings); SequenceStartupScope.ValidateRealTargets(this);
                if (!IsSelected(profiles.ActiveProfile.SafetyMonitorSettings.Id))
                    throw new InvalidOperationException("先在 N.I.N.A. Profile 选择安全监视器；启动卡片不会替换设备或启用网络天气源。"); }
            catch (InvalidOperationException ex) { Issues.Add(ex.Message); }
        return Issues.Count == 0;
    }
    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!StartMonitoring) return;
        if (!Validate()) throw new InvalidOperationException(string.Join(" ", Issues));
        using var reservation = await host.ReserveNightAsync(this, true, token);
        var profile = profiles.ActiveProfile;
        var selectedId = profile.SafetyMonitorSettings.Id;
        if (!IsSelected(selectedId)) throw new InvalidOperationException("未选择安全监视器；不能连接。");
        var before = monitor.GetInfo();
        if (before.Connected && before.DeviceId != selectedId)
            throw new InvalidOperationException("已连接的安全监视器身份不符；未断开或替换它。");
        // Await the native owner operation to completion, including cancellation;
        // never abandon a Connect task that may finish after the lease is released.
        if (!before.Connected && !await monitor.Connect())
            throw new InvalidOperationException("安全监视器连接失败；未允许后续观测继续。");
        token.ThrowIfCancellationRequested();
        var after = monitor.GetInfo();
        if (!ReferenceEquals(profile, profiles.ActiveProfile) || profiles.ActiveProfile.SafetyMonitorSettings.Id != selectedId ||
            !after.Connected || after.DeviceId != selectedId)
            throw new InvalidOperationException("安全监视器连接或 Profile 身份读回不一致；不能继续。");
        ObservationStatusProgress.Wrap(progress).Report(new ApplicationStatus { Source = "OpenAstroSpec 启动", Status = "安全监视器已连接 · 待安全判定" });
    }
    public override object Clone()
    {
        var clone = new UvexStartSafetyMonitorItem(profiles, host, monitor, settings) { StartMonitoring = StartMonitoring };
        clone.CopyMetaData(this); return clone;
    }
}
