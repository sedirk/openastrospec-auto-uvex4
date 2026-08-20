using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace UvexAdv.Phd2.Tests;

internal sealed class FakePhd2Server : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<FakePhd2Session, CancellationToken, Task> scenario;
    private readonly Task serverTask;

    public FakePhd2Server(Func<FakePhd2Session, CancellationToken, Task> scenario)
    {
        this.scenario = scenario;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        serverTask = RunAsync(lifetime.Token);
    }

    public int Port { get; }

    public ConcurrentQueue<string> ReceivedMethods { get; } = new();

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        listener.Stop();
        try
        {
            await serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (SocketException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using var session = new FakePhd2Session(client, ReceivedMethods);
            await scenario(session, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class FakePhd2Session : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly TcpClient client;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly ConcurrentQueue<string> receivedMethods;

    public FakePhd2Session(TcpClient client, ConcurrentQueue<string> receivedMethods)
    {
        this.client = client;
        this.receivedMethods = receivedMethods;
        var stream = client.GetStream();
        reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\r\n",
        };
    }

    public async Task<JsonElement> ReadRequestAsync(CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            throw new EndOfStreamException("The fake PHD2 client closed the connection.");
        }

        using var document = JsonDocument.Parse(line);
        var request = document.RootElement.Clone();
        if (request.TryGetProperty("method", out var method))
        {
            receivedMethods.Enqueue(method.GetString()!);
        }

        return request;
    }

    public Task ReplyResultAsync(
        JsonElement request,
        object? result,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(
            new
            {
                jsonrpc = "2.0",
                result,
                id = request.GetProperty("id").GetInt64(),
            },
            cancellationToken);
    }

    public Task ReplyErrorAsync(
        JsonElement request,
        int code,
        string message,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(
            new
            {
                jsonrpc = "2.0",
                error = new
                {
                    code,
                    message,
                },
                id = request.GetProperty("id").GetInt64(),
            },
            cancellationToken);
    }

    public Task SendEventAsync(object payload, CancellationToken cancellationToken = default)
    {
        return SendAsync(payload, cancellationToken);
    }

    public void CloseConnection()
    {
        client.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        writer.Dispose();
        reader.Dispose();
        client.Dispose();
        writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }
}
