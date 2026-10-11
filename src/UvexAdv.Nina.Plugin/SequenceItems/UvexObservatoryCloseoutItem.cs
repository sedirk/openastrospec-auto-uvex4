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
[ExportMetadata("Description", "拖到光谱整夜容器最后。开启：普通结束/失败统一收口；关闭：确认停止后受保护开顶待机。安全收口不能关闭。")]
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
            if (Parent is UvexNightSequenceContainer { IsActive: true })
                throw new InvalidOperationException("本夜收口策略已经锁定，运行中不可修改。");
            closeAfterTargets = value;
            RaisePropertyChanged();
        }
    }

    [JsonIgnore] public bool CanEditPlan => Parent is not UvexNightSequenceContainer { IsActive: true };
    internal void RefreshPlanEditability() => RaisePropertyChanged(nameof(CanEditPlan));

    [JsonIgnore] public IList<string> Issues { get; } = new ObservableCollection<string>();
    public bool Validate()
    {
        Issues.Clear();
        if (Parent is not UvexNightSequenceContainer night || !ReferenceEquals(night.Items.LastOrDefault(), this))
            Issues.Add("关顶收口指令只能放在光谱整夜容器最后，由该容器统一执行；不能放到每个目标内或容器外。");
        return Issues.Count == 0;
    }

    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Validate() || Parent is not UvexNightSequenceContainer { IsActive: true })
            throw new InvalidOperationException("收口指令没有有效的整夜执行权。");
        // The immutable choice is honored in the owner's finally/standby path,
        // even when a failed target prevents this marker from being reached.
        progress.Report(new ApplicationStatus { Source = "OpenAstroSpec 整夜",
            Status = "目标执行已结束；由整夜容器按启动时锁定的收口选择处理。" });
        return Task.CompletedTask;
    }

    public override object Clone()
    {
        var clone = new UvexObservatoryCloseoutItem { CloseAfterTargets = CloseAfterTargets };
        clone.CopyMetaData(this);
        return clone;
    }
}
