using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Nina.Plugin;

internal sealed record PhotometryPipeRequest(int ProtocolVersion, Guid ProfileId, Guid MasterProfileId,
    Guid MasterSessionId, Guid? WorkerSessionId, string Method, string Path, string? Body);
internal sealed record PhotometryPipeResponse(int StatusCode, string Body, string ContentType,
    Dictionary<string, string> Headers, QhyNinaWorkerIdentity Identity);

internal static class PhotometryPipeProtocol
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal const int MaximumMessageBytes = 4 * 1024 * 1024;

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > MaximumMessageBytes) throw new InvalidDataException("Invalid worker message length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty worker message.");
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("Worker message is too large.");
        var prefix = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }
}

/// <summary>Local, current-user-only transport. No listening TCP socket and no
/// forwarding to Advanced API's unrestricted equipment endpoints.</summary>
internal sealed class PhotometryPipeServer : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task loop;
    private readonly NamedPipeServerStream pipe;
    private int disposed;
    public PhotometryPipeServer(Guid profileId,
        Func<PhotometryPipeRequest, CancellationToken, Task<PhotometryPipeResponse>> dispatch)
    {
        // Bind synchronously so a duplicate Profile endpoint fails at Enable,
        // not later in an unobserved background task.
        pipe = new NamedPipeServerStream(NinaInstancePolicy.WorkerPipeName(profileId), PipeDirection.InOut,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        loop = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(lifetime.Token);
                    using var bounded = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    bounded.CancelAfter(TimeSpan.FromSeconds(45));
                    var request = await PhotometryPipeProtocol.ReadAsync<PhotometryPipeRequest>(pipe, bounded.Token);
                    var response = await dispatch(request, bounded.Token);
                    await PhotometryPipeProtocol.WriteAsync(pipe, response, bounded.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidDataException)
                {
                    // A disconnected caller cannot take the worker's local lease
                    // watchdog down. Native job state remains in the coordinator.
                }
                finally { if (pipe.IsConnected) pipe.Disconnect(); }
            }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        _ = loop.ContinueWith(_ => { pipe.Dispose(); lifetime.Dispose(); }, TaskScheduler.Default);
    }
}

internal sealed class PhotometryPipeHttpHandler(Guid workerProfileId, Guid masterProfileId) : HttpMessageHandler
{
    private readonly Guid masterSession = Guid.NewGuid();
    private readonly SemaphoreSlim serial = new(1, 1);
    private QhyNinaWorkerIdentity? pinned;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            if (masterProfileId == Guid.Empty || masterProfileId == workerProfileId)
                throw new InvalidOperationException("Dual N.I.N.A. requires two distinct bound Profile IDs.");
            if (pinned is null)
            {
                var hello = await ExchangeAsync("GET", "/api/v1/health", null, cancellationToken);
                ValidateIdentity(hello.Identity);
                if (hello.StatusCode != 200) throw new InvalidOperationException(hello.Body);
                pinned = hello.Identity;
            }
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var reply = await ExchangeAsync(request.Method.Method, request.RequestUri!.PathAndQuery, body, cancellationToken);
            ValidateIdentity(reply.Identity);
            var response = new HttpResponseMessage((HttpStatusCode)reply.StatusCode);
            response.Content = reply.ContentType == "image/png"
                ? new ByteArrayContent(Convert.FromBase64String(reply.Body))
                : new StringContent(reply.Body, Encoding.UTF8, reply.ContentType);
            foreach (var header in reply.Headers) response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return response;
        }
        finally { serial.Release(); }
    }

    private void ValidateIdentity(QhyNinaWorkerIdentity current)
    {
        if (current.ProfileId != workerProfileId || current.MasterProfileId != masterProfileId ||
            current.SessionId == Guid.Empty || current.ProcessId <= 0 || current.ProcessId == Environment.ProcessId ||
            !QhyServiceConfigurationProof.IsSha256(current.ConfigurationSha256))
            throw new InvalidDataException("PHOTOMETRY_WORKER_IDENTITY_MISMATCH: Incorrect worker profile, process or master binding.");
        if (pinned is not null && current != pinned)
            throw new InvalidOperationException("PHOTOMETRY_WORKER_SESSION_CHANGED: Worker restarted or changed configuration; no automatic re-adoption in the active run.");
    }

    private async Task<PhotometryPipeResponse> ExchangeAsync(string method, string path, string? body, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", NinaInstancePolicy.WorkerPipeName(workerProfileId),
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, token);
        await PhotometryPipeProtocol.WriteAsync(pipe, new PhotometryPipeRequest(1, workerProfileId, masterProfileId,
            masterSession, pinned?.SessionId, method, path, body), token);
        return await PhotometryPipeProtocol.ReadAsync<PhotometryPipeResponse>(pipe, token);
    }
}
