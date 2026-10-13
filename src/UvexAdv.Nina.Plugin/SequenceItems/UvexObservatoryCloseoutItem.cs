using System.ComponentModel.Composition;
using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using Newtonsoft.Json;

namespace UvexAdv.Nina.Plugin.SequenceItems;

/// <summary>Declarative terminal instruction; the reserved night is the only mechanical owner.</summary>
[ExportMetadata("Name", "OpenAstroSpec · 关顶与整夜收口（可选）")]
[ExportMetadata("Description", "放到原生结束区域（单一光谱整夜），兼容旧版整夜末尾。开启：普通结束/失败统一收口；关闭：独立安全待机。安全收口不能关闭。")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
[SupportedOSPlatform("windows")]
public sealed class UvexObservatoryCloseoutItem : SequenceItem, IValidatable
{
    private bool closeAfterTargets = true;
    [JsonProperty]
    public bool CloseAfterTargets
    {
        get => closeAfterTargets;
        set
        {
            if (NativeNightEndArea.Owner(this) is { IsActive: true })
                throw new InvalidOperationException("本夜收口策略已经锁定，运行中不可修改。");
            closeAfterTargets = value;
            RaisePropertyChanged();
        }
    }

    [JsonIgnore] public bool CanEditPlan => NativeNightEndArea.Owner(this) is not { IsActive: true };
    [JsonIgnore] public UvexNightSequenceContainer? Night => NativeNightEndArea.Owner(this);
    internal void RefreshPlanEditability() => RaisePropertyChanged(nameof(CanEditPlan));

    public override void AfterParentChanged()
    {
        base.AfterParentChanged();
        RaisePropertyChanged(nameof(Night));
        RefreshPlanEditability();
    }

    [JsonIgnore] public IList<string> Issues { get; } = new ObservableCollection<string>();
    public bool Validate()
    {
        Issues.Clear();
        var night = NativeNightEndArea.Owner(this);
        if (night is null || (Parent is UvexNightSequenceContainer && !ReferenceEquals(night.Items.LastOrDefault(), this)))
            Issues.Add("关顶收口选择放在唯一光谱整夜的原生结束区域，或旧版整夜容器最后；不能放进目标、循环或并行容器。");
        if (night is not null && NativeNightEndArea.Choices(night).Count() > 1)
            Issues.Add("整夜只能有一个启用的收口选择；请移走旧卡，不要在两处设置。");
        return Issues.Count == 0;
    }

    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Validate() || NativeNightEndArea.Owner(this) is not { } night ||
            (Parent is UvexNightSequenceContainer && !night.IsActive))
            throw new InvalidOperationException("收口指令没有有效的整夜执行权。");
        // The immutable choice is honored in the owner's finally/standby path,
        // even when a failed target prevents this marker from being reached.
        progress.Report(new ApplicationStatus { Source = "OpenAstroSpec 整夜",
            Status = Parent is UvexNightSequenceContainer
                ? "目标执行已结束；由整夜容器按启动时锁定的收口选择处理。"
                : night.NightState });
        return Task.CompletedTask;
    }

    public override object Clone()
    {
        var clone = new UvexObservatoryCloseoutItem { CloseAfterTargets = CloseAfterTargets };
        clone.CopyMetaData(this);
        return clone;
    }
}
