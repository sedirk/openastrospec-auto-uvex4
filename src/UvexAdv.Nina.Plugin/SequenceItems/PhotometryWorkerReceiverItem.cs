using System.ComponentModel.Composition;
using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;
using Newtonsoft.Json;

namespace UvexAdv.Nina.Plugin.SequenceItems;

/// <summary>A deliberately narrow worker sequence entry. Arbitrary user scripts,
/// mount items, dithering and shared-device triggers are not imported into it.</summary>
[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "OpenAstroSpec 测光协同接收")]
[ExportMetadata("Description", "等待已绑定光谱主控的测光作业；只操作测光相机、滤镜轮和电调焦。取消时停止测光，不操作共享设备。")]
[ExportMetadata("Category", "OpenAstroSpec Auto")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class PhotometryWorkerReceiverItem : SequenceItem
{
    private readonly PhotometryWorkerHost host;
    [ImportingConstructor]
    public PhotometryWorkerReceiverItem(PhotometryWorkerHost host) { this.host = host; }
    private PhotometryWorkerReceiverItem(PhotometryWorkerReceiverItem copy) { host = copy.host; CopyMetaData(copy); }
    public override object Clone() => new PhotometryWorkerReceiverItem(this);
    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        host.BeginReceiver();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var job = host.LatestJob;
                progress.Report(new ApplicationStatus { Source = "OpenAstroSpec Photometry", Status =
                    job is null ? host.Status : $"{job.RequestedTarget}: {job.State}, {job.TotalFrameCount} frames" });
                await Task.Delay(500, token);
            }
        }
        finally { await host.EndReceiverAsync(); }
    }
}
