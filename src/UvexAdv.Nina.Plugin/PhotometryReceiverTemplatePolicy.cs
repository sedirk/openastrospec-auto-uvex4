using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using UvexAdv.Nina.Plugin.SequenceItems;

namespace UvexAdv.Nina.Plugin;

internal static class PhotometryReceiverTemplatePolicy
{
    internal const string Contract = "photometry-receiver-v1; one receiver; sequential; no other leaves, conditions or triggers";
    internal static void Validate(ISequenceItem root)
    {
        var visited = new HashSet<ISequenceItem>(ReferenceEqualityComparer.Instance);
        var receivers = 0;
        void Visit(ISequenceItem item)
        {
            if (!visited.Add(item) || visited.Count > 100)
                throw new InvalidOperationException("PHOTOMETRY_TEMPLATE_INVALID: Cyclic or oversized worker template.");
            if (item is ITriggerable { Triggers.Count: > 0 } || item is IConditionable { Conditions.Count: > 0 })
                throw new InvalidOperationException("PHOTOMETRY_TEMPLATE_FORBIDDEN: Worker receiver cannot contain independent conditions or triggers.");
            if (item is PhotometryWorkerReceiverItem) { receivers++; return; }
            if (item is not ISequenceContainer container || item.GetType().FullName is not
                ("NINA.Sequencer.Container.SequenceRootContainer" or "NINA.Sequencer.Container.StartAreaContainer" or
                 "NINA.Sequencer.Container.TargetAreaContainer" or "NINA.Sequencer.Container.EndAreaContainer" or
                 "NINA.Sequencer.Container.SequentialContainer"))
                throw new InvalidOperationException("PHOTOMETRY_TEMPLATE_FORBIDDEN: Only the reviewed receiver in native sequential containers is permitted.");
            foreach (var child in container.GetItemsSnapshot()) Visit(child);
        }
        Visit(root);
        if (receivers != 1) throw new InvalidOperationException("PHOTOMETRY_TEMPLATE_INVALID: Exactly one photometry receiver is required.");
    }
}
