using System.ComponentModel.Composition;
using System.Runtime.Versioning;
using NINA.Core.Enum;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Utility;
using Newtonsoft.Json;

namespace UvexAdv.Nina.Plugin.SequenceItems;

/// <summary>Native editor composition only; device closeout remains owned by the night.</summary>
[SupportedOSPlatform("windows")]
internal static class NativeNightEndArea
{
    internal static SequenceRootContainer? Root(UvexNightSequenceContainer night) =>
        night.Parent is TargetAreaContainer { Parent: SequenceRootContainer root } ? root : null;

    internal static UvexNightSequenceContainer? Night(EndAreaContainer? end)
    {
        if (end?.Parent is not SequenceRootContainer root) return null;
        var nights = root.Items.OfType<TargetAreaContainer>().SelectMany(x => x.Items)
            .OfType<UvexNightSequenceContainer>().ToArray();
        return nights.Length == 1 ? nights[0] : null;
    }

    internal static EndAreaContainer? End(UvexNightSequenceContainer night) =>
        Root(night)?.Items.OfType<EndAreaContainer>().SingleOrDefault();

    internal static IEnumerable<UvexObservatoryCloseoutItem> Choices(UvexNightSequenceContainer night) =>
        night.Items.OfType<UvexObservatoryCloseoutItem>()
            .Concat(End(night)?.Items.OfType<UvexObservatoryCloseoutItem>() ?? [])
            .Where(x => x.Status != SequenceEntityStatus.DISABLED);

    internal static UvexNightSequenceContainer? Owner(UvexObservatoryCloseoutItem item) =>
        item.Parent as UvexNightSequenceContainer ?? Night(item.Parent as EndAreaContainer);

    internal static IEnumerable<string> Issues(UvexNightSequenceContainer night)
    {
        var end = End(night);
        if (end is null) yield break;
        if (end.Items.Any(x => x is not Annotation) && !ReferenceEquals(Night(end), night))
            yield return "结束区域的光谱收口选择需要唯一一个光谱整夜容器；多夜容器请各自保留末尾收口卡。";
        if (end.Triggers.Any(x => x.Status != SequenceEntityStatus.DISABLED))
            yield return "结束区域的触发器请放入受保护的附加收尾组，不能在开顶待机边界外启动设备动作。";
        foreach (var item in end.Items.Where(x => x.Status != SequenceEntityStatus.DISABLED))
        {
            if (item is UvexObservatoryCloseoutItem or Annotation) continue;
            if (item.GetType() == typeof(SequentialContainer) && item is SequentialContainer group &&
                group.Conditions.OfType<UvexNightClosedCondition>().Any(x => x.Status != SequenceEntityStatus.DISABLED)) continue;
            yield return "附加收尾请放入原生顺序指令集，并保留“光谱正常关顶后执行一次”条件；开顶待机时不能并行操作设备。";
        }
    }
}

[ExportMetadata("Name", "OpenAstroSpec · 光谱正常关顶后执行一次")]
[ExportMetadata("Description", "用于结束区域的原生顺序指令集。仅本轮正常收口确认后执行一次；开顶待机、取消、故障或尚未执行时跳过附加收尾。不负责关顶。")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[Export(typeof(ISequenceCondition))]
[JsonObject(MemberSerialization.OptIn)]
[SupportedOSPlatform("windows")]
public sealed class UvexNightClosedCondition : SequenceCondition
{
    private string? attemptedJournal;
    private string? activeJournal;
    public override bool Check(ISequenceItem previousItem, ISequenceItem nextItem) =>
        Parent?.GetType() == typeof(SequentialContainer) && Parent.Iterations < 1 &&
        Parent.Parent is EndAreaContainer end &&
        NativeNightEndArea.Night(end) is { IsActive: false, NativeEndInstructionsReady: true } night &&
        !string.IsNullOrEmpty(night.JournalPath) &&
        (night.JournalPath == activeJournal || night.JournalPath != attemptedJournal);

    // Charge the native block before its first action, so a partially failed
    // closeout group cannot be replayed by resetting only its child statuses.
    public override void SequenceBlockStarted() =>
        attemptedJournal = activeJournal = NativeNightEndArea.Night(Parent?.Parent as EndAreaContainer)?.JournalPath;

    public override void SequenceBlockTeardown() => activeJournal = null;

    public override object Clone()
    {
        var copy = new UvexNightClosedCondition();
        copy.CopyMetaData(this);
        return copy;
    }
}
