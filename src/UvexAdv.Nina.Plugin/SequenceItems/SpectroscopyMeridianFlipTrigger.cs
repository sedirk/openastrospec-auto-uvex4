using System.ComponentModel.Composition;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.MeridianFlip;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using Newtonsoft.Json;

namespace UvexAdv.Nina.Plugin.SequenceItems;

[ExportMetadata("Name", "OpenAstroSpec · 光谱中天翻转")]
[ExportMetadata("Description", "逐帧使用 N.I.N.A. 翻转时机；保存并停止两路采集后翻转，重新定位/入缝/导星，仅续拍剩余帧。")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceTrigger))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class SpectroscopyMeridianFlipTrigger : MeridianFlipTrigger
{
    [ImportingConstructor]
    public SpectroscopyMeridianFlipTrigger(IProfileService profiles, ICameraMediator camera,
        ITelescopeMediator telescope, IFocuserMediator focuser, IApplicationStatusMediator status,
        IMeridianFlipVMFactory factory) : base(profiles, camera, telescope, focuser, status, factory) { }

    private SpectroscopyMeridianFlipTrigger(SpectroscopyMeridianFlipTrigger copy) : base(copy)
    { BoundaryReserveSeconds = copy.BoundaryReserveSeconds; }

    // Time for read/save/guide-window and the worker's frame boundary. The
    // mount's own limit remains authoritative; this is not a limit override.
    [JsonProperty] public double BoundaryReserveSeconds { get; set; } = 120;
    [JsonIgnore] public string FlipStatus { get; private set; } = "等待光谱逐帧边界；模拟不会翻转设备。";

    public override object Clone() => new SpectroscopyMeridianFlipTrigger(this);

    // Native stage markers represent entire stages, not individual exposures.
    // Only the owned runner below may dispatch a flip, after both owners join.
    public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => false;
    public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) =>
        throw new InvalidOperationException("光谱翻转只由 UVEX4 目标的逐帧边界调用，不能当作独立机械动作运行。");

    public override bool Validate()
    {
        Issues.Clear();
        if (!double.IsFinite(BoundaryReserveSeconds) || BoundaryReserveSeconds is < 30 or > 900)
            Issues.Add("保存/导星/测光边界余量须为 30–900 秒。");
        if (!UseSideOfPier) Issues.Add("光谱翻转须在 N.I.N.A. 翻转设置中启用镜筒侧检查。");
        if (!double.IsFinite(MinutesAfterMeridian) || !double.IsFinite(MaxMinutesAfterMeridian) ||
            !double.IsFinite(PauseTimeBeforeMeridian) || MinutesAfterMeridian < 0 ||
            MaxMinutesAfterMeridian < MinutesAfterMeridian || MaxMinutesAfterMeridian > 60 ||
            PauseTimeBeforeMeridian is < 0 or > 60)
            Issues.Add("N.I.N.A. 翻转时间范围无效（最早/最晚顺序，范围 0–60 分钟）。");
        return Issues.Count == 0;
    }

    internal string ConfigurationKey => $"{profileService.ActiveProfile.Id}|{MinutesAfterMeridian:R}|{MaxMinutesAfterMeridian:R}|{PauseTimeBeforeMeridian:R}|{UseSideOfPier}|{BoundaryReserveSeconds:R}|{profileService.ActiveProfile.MeridianFlipSettings.SettleTime}";
    internal TimeSpan WaitBeforeFlip => CalculateMinimumTimeRemaining();
    internal double SettleSeconds => profileService.ActiveProfile.MeridianFlipSettings.SettleTime;
    internal NINA.Core.Enum.PierSide? RequestedSide { get; private set; }
    internal bool IsDue(double exposureSeconds)
    {
        if (Status == NINA.Core.Enum.SequenceEntityStatus.DISABLED) return false;
        if (!Validate()) throw new InvalidOperationException(string.Join(" ", Issues));
        var info = telescopeMediator.GetInfo();
        if (!info.Connected || info.Slewing || !info.TrackingEnabled || info.AtPark || info.AtHome ||
            !double.IsFinite(info.TimeToMeridianFlip) || info.SideOfPier == NINA.Core.Enum.PierSide.pierUnknown)
            throw new InvalidOperationException("翻转时机无法核验：赤道仪须连接、跟踪、空闲，并报告有效镜筒侧及中天时间。");
        var due = base.ShouldTrigger(null!, new ExposureBoundary(exposureSeconds + BoundaryReserveSeconds));
        if (due) RequestedSide = info.SideOfPier;
        return due;
    }

    internal void SetStatus(string value)
    {
        FlipStatus = value;
        RaisePropertyChanged(nameof(FlipStatus));
    }

    internal static IReadOnlyList<ISequenceTrigger> InScope(ISequenceContainer container)
    {
        var result = new List<ISequenceTrigger>();
        for (var current = container; current is not null; current = current.Parent)
            if (current is ITriggerable triggerable)
                result.AddRange(triggerable.GetTriggersSnapshot().Where(x => x.Status != NINA.Core.Enum.SequenceEntityStatus.DISABLED));
        return result;
    }

    internal static IEnumerable<string> ScopeIssues(ISequenceContainer container)
    {
        var scope = InScope(container);
        if (scope.Any(x => x is not SpectroscopyMeridianFlipTrigger))
            yield return "光谱目标只支持 OpenAstroSpec · 光谱中天翻转。普通翻转、dither、对焦触发器不能穿透光谱目标；请放到其他分组。";
        if (scope.OfType<SpectroscopyMeridianFlipTrigger>().Count() > 1)
            yield return "同一光谱目标只能继承一个光谱翻转触发器，不要同时在全局和目标重复添加。";
        if (scope.OfType<SpectroscopyMeridianFlipTrigger>().Any() && container is UvexTargetObservationContainer &&
            container.Parent is not UvexNightSequenceContainer)
            yield return "启用光谱翻转的目标必须放入光谱整夜序列，以保留翻转期间设备执行权和安全收口。";
        if (scope.OfType<SpectroscopyMeridianFlipTrigger>().Any() && container is UvexTargetObservationContainer target && target.Conditions.Count != 0)
            yield return "启用翻转时，目标内部不再叠加循环条件；使用科学帧数和整夜截止/晨光条件，避免分段重启循环计数。";
    }

    private sealed class ExposureBoundary(double seconds) : SequenceItem
    {
        public override TimeSpan GetEstimatedDuration() => TimeSpan.FromSeconds(seconds);
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) => throw new NotSupportedException();
        public override object Clone() => new ExposureBoundary(seconds);
    }
}
