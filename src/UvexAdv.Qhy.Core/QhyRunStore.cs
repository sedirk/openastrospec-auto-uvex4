using System.Text.Json;
using System.Text.Json.Serialization;

namespace UvexAdv.Qhy.Core;

public sealed record StoredQhyFrame(QhyFrameRecord Record, QhyPreview Preview);

public sealed class QhyRunStore
{
    private readonly string root;
    private readonly SemaphoreSlim manifestGate = new(1, 1);
    private readonly Dictionary<Guid, long> manifestRevisions = [];
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private readonly JsonSerializerOptions jsonLineOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public QhyRunStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A QHY data root is required.", nameof(root));
        this.root = Path.GetFullPath(root);
    }

    public string GetManifestPath(string observationRunId, Guid jobId) =>
        Path.Combine(GetJobDirectory(observationRunId, jobId), "manifest.json");

    public string GetFrameIndexPath(string observationRunId, Guid jobId) =>
        Path.Combine(GetJobDirectory(observationRunId, jobId), "frames.jsonl");

    public async Task<StoredQhyFrame> StoreFrameAsync(
        QhyJobSnapshot job,
        QhyFrame frame,
        QhyFrameMetrics metrics,
        int sequenceNumber,
        string role,
        CancellationToken cancellationToken)
    {
        var frameId = Guid.NewGuid();
        var directory = GetJobDirectory(job.ObservationRunId, job.Id);
        var rawDirectory = Path.Combine(directory, "raw");
        var previewDirectory = Path.Combine(directory, "preview");
        Directory.CreateDirectory(rawDirectory);
        Directory.CreateDirectory(previewDirectory);
        var timestamp = frame.ExposureStartedUtc.UtcDateTime.ToString("yyyyMMddTHHmmss.fffffffZ");
        var basename = $"{sequenceNumber:D6}_{Sanitize(role)}_{timestamp}_{frameId:N}";
        var fitsPath = Path.Combine(rawDirectory, basename + ".fits");
        var previewPath = Path.Combine(previewDirectory, basename + ".png");
        var sha256 = await QhyFitsCodec.WriteAsync(
            fitsPath,
            frame,
            job.Id,
            job.ObservationRunId,
            frameId,
            sequenceNumber,
            role,
            job.RequestedTarget,
            job.TargetRightAscensionDegrees,
            job.TargetDeclinationDegrees,
            job.CoordinateEpoch,
            cancellationToken).ConfigureAwait(false);
        var preview = QhyPreviewEncoder.Encode(job.Id, frameId, frame, metrics);
        await File.WriteAllBytesAsync(previewPath, preview.PngBytes, cancellationToken).ConfigureAwait(false);
        var record = new QhyFrameRecord(
                frameId,
                sequenceNumber,
                role,
                fitsPath,
                previewPath,
                sha256,
                frame.ExposureStartedUtc,
                frame.MidpointUtc,
                frame.ExposureEndedUtc,
                frame.Settings,
                metrics);
        await AppendFrameIndexAsync(job, record, cancellationToken).ConfigureAwait(false);
        return new StoredQhyFrame(record, preview);
    }

    public async Task<bool> PersistManifestAsync(QhyJobSnapshot snapshot, CancellationToken cancellationToken)
    {
        await manifestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var revision = snapshot.Revision;
            if (manifestRevisions.TryGetValue(snapshot.Id, out var persistedRevision) && revision <= persistedRevision)
            {
                return false;
            }

            var path = GetManifestPath(snapshot.ObservationRunId, snapshot.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + ".partial";
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
            manifestRevisions[snapshot.Id] = revision;
            return true;
        }
        finally
        {
            manifestGate.Release();
        }
    }

    private async Task AppendFrameIndexAsync(
        QhyJobSnapshot job,
        QhyFrameRecord record,
        CancellationToken cancellationToken)
    {
        var path = GetFrameIndexPath(job.ObservationRunId, job.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(record, jsonLineOptions) + Environment.NewLine;
        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private string GetJobDirectory(string observationRunId, Guid jobId) =>
        Path.Combine(root, "runs", Sanitize(observationRunId), $"qhy-{jobId:N}");

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unnamed";
        var safe = new string(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_').ToArray());
        return safe.Length <= 96 ? safe : safe[..96];
    }
}
