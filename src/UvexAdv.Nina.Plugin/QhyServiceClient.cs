using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using UvexAdv.Qhy.Core;

namespace UvexAdv.Nina.Plugin;

internal sealed class QhyServiceClient : IDisposable
{
    private const string AutomationActor = "uvex-adv-nina-plugin";
    private static readonly TimeSpan AmbiguousStartRecoveryTimeout = TimeSpan.FromSeconds(25);
    private readonly HttpClient http;
    private readonly ConcurrentDictionary<Guid, QhyOwnerSession> ownerSessions = new();

    public QhyServiceClient(string serviceUrl)
        : this(serviceUrl, handler: null)
    {
    }

    internal QhyServiceClient(string serviceUrl, HttpMessageHandler? handler)
    {
        var uri = new Uri(serviceUrl, UriKind.Absolute);
        if (!uri.IsLoopback)
        {
            throw new ArgumentException("The first real QHY integration is restricted to a loopback service URL.", nameof(serviceUrl));
        }

        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        http.BaseAddress = uri;
        http.Timeout = TimeSpan.FromSeconds(15);
    }

    public bool HasOwnerSession(Guid jobId) => ownerSessions.ContainsKey(jobId);

    public Task<QhyCameraStatus?> GetCameraAsync(CancellationToken cancellationToken) =>
        http.GetFromJsonAsync<QhyCameraStatus>("/api/v1/camera", cancellationToken);

    public async Task<QhyServiceHealth> GetHealthAsync(CancellationToken cancellationToken) =>
        await http.GetFromJsonAsync<QhyServiceHealth>("/api/v1/health", cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("QHY service returned an empty health/configuration proof.");

    public async Task<QhyCameraStatus> EnsureCameraConnectedAsync(CancellationToken cancellationToken)
    {
        var current = await GetCameraAsync(cancellationToken).ConfigureAwait(false);
        if (current?.Connected == true) return current;

        using var response = await http.PostAsync("/api/v1/camera/connect", null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<QhyCameraStatus>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("QHY service returned no camera state after connect.");
    }

    public Task<QhyJobSnapshot> StartAcquisitionAsync(AcquisitionJobRequest request, CancellationToken cancellationToken) =>
        PostJobAsync("/api/v1/jobs/acquisition", request, cancellationToken);

    public Task<QhyJobSnapshot> StartPhotometryAsync(PhotometryJobRequest request, CancellationToken cancellationToken) =>
        PostJobAsync("/api/v1/jobs/photometry", request, cancellationToken);

    public async Task<QhyJobSnapshot?> FindJobAsync(
        string observationRunId,
        QhyJobKind kind,
        string clientRequestId,
        CancellationToken cancellationToken)
    {
        var path = $"/api/v1/jobs/lookup?observationRunId={Uri.EscapeDataString(observationRunId)}&kind={Uri.EscapeDataString(kind.ToString())}&clientRequestId={Uri.EscapeDataString(clientRequestId)}";
        using var response = await http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var snapshot = await response.Content.ReadFromJsonAsync<QhyJobSnapshot>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("QHY service returned an empty lookup response.");
        ForgetOwnerIfTerminal(snapshot);
        return snapshot;
    }

    public async Task<QhyJobSnapshot> StartOrAdoptAcquisitionAsync(
        AcquisitionJobRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(request.ClientRequestId, nameof(request));
        try
        {
            return await StartAcquisitionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecoverCancelledAmbiguousStartBestEffortAsync(request, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception first) when (IsAmbiguousStartFailure(first))
        {
            try
            {
                return await StartAcquisitionAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RecoverCancelledAmbiguousStartBestEffortAsync(request, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception retry) when (IsAmbiguousStartFailure(retry))
            {
                var recovery = await TryRecoverAmbiguousStartAsync(request, CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "QHY acquisition start remained ambiguous after an idempotent retry; any discovered accepted job was forced to a checked safe terminal state.",
                    recovery is null ? new AggregateException(first, retry) : new AggregateException(first, retry, recovery));
            }
        }
    }

    public async Task<QhyJobSnapshot> StartOrAdoptPhotometryAsync(
        PhotometryJobRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(request.ClientRequestId, nameof(request));
        try
        {
            return await StartPhotometryAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecoverCancelledAmbiguousStartBestEffortAsync(request, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception first) when (IsAmbiguousStartFailure(first))
        {
            try
            {
                return await StartPhotometryAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RecoverCancelledAmbiguousStartBestEffortAsync(request, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception retry) when (IsAmbiguousStartFailure(retry))
            {
                var recovery = await TryRecoverAmbiguousStartAsync(request, CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "QHY photometry start remained ambiguous after an idempotent retry; any discovered accepted job was forced to a checked safe terminal state.",
                    recovery is null ? new AggregateException(first, retry) : new AggregateException(first, retry, recovery));
            }
        }
    }

    public async Task<QhyJobSnapshot?> GetJobAsync(Guid id, CancellationToken cancellationToken)
    {
        var snapshot = await http.GetFromJsonAsync<QhyJobSnapshot>($"/api/v1/jobs/{id:D}", cancellationToken).ConfigureAwait(false);
        if (snapshot is not null) ForgetOwnerIfTerminal(snapshot);
        return snapshot;
    }

    public async Task<QhyJobSnapshot> WaitForQuiescentOrTerminalAsync(
        Guid id,
        Func<QhyJobSnapshot, Task>? onPoll,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await GetJobAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"QHY service lost job {id:D}.");
            if (onPoll is not null) await onPoll(snapshot).ConfigureAwait(false);
            if (snapshot.State == QhyJobState.PausedNeedsAttention || IsTerminal(snapshot.State)) return snapshot;
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<QhyJobSnapshot> WaitForFirstFrameOrTerminalAsync(
        Guid id,
        Func<QhyJobSnapshot, Task>? onPoll,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await GetJobAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"QHY service lost job {id:D}.");
            if (onPoll is not null) await onPoll(snapshot).ConfigureAwait(false);
            // A frame is published before its quality gate has necessarily been
            // evaluated. Only acknowledge that exact frame after the service marks
            // it healthy; PausedNeedsAttention remains a quiescent failure result.
            var evaluatedHealthyFrameAvailable =
                snapshot.LastEvaluatedFrameId is { } evaluatedFrameId &&
                snapshot.LastFramePassedQualityGate == true &&
                snapshot.Frames.Any(frame => frame.FrameId == evaluatedFrameId);
            if (evaluatedHealthyFrameAvailable || snapshot.State == QhyJobState.PausedNeedsAttention || IsTerminal(snapshot.State)) return snapshot;
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<QhyJobSnapshot> WaitForCheckedTerminalAsync(
        Guid id,
        TimeSpan timeout,
        Func<QhyJobSnapshot, Task>? onPoll,
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var snapshot = await GetJobAsync(id, bounded.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"QHY service lost job {id:D}.");
                if (onPoll is not null) await onPoll(snapshot).ConfigureAwait(false);
                if (IsTerminal(snapshot.State)) return snapshot;
                await Task.Delay(250, bounded.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"QHY job {id:D} did not reach a checked terminal state within {timeout.TotalSeconds:F0} seconds.");
        }
    }

    public async Task<QhyJobSnapshot> WaitForPausedOrTerminalAsync(
        Guid id,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var snapshot = await GetJobAsync(id, bounded.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"QHY service lost job {id:D} while confirming pause.");
                if (snapshot.State is QhyJobState.Paused or QhyJobState.PausedNeedsAttention || IsTerminal(snapshot.State))
                {
                    return snapshot;
                }
                await Task.Delay(200, bounded.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"QHY job {id:D} did not reach a paused or terminal state within {timeout.TotalSeconds:F0} seconds.");
        }
    }

    public async Task<byte[]?> GetPreviewPngAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"/api/v1/jobs/{id:D}/preview", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<QhyJobSnapshot> PauseAsync(Guid id, CancellationToken cancellationToken)
    {
        var owner = RequireOwnerSession(id);
        return PostOwnedControlAsync(
            id,
            "pause",
            new QhyOwnerControlRequest(owner.OwnerToken, AutomationActor),
            owner,
            cancellationToken);
    }

    public Task<QhyJobSnapshot> ResumeAsync(Guid id, CancellationToken cancellationToken)
    {
        var owner = RequireOwnerSession(id);
        return PostOwnedControlAsync(
            id,
            "resume",
            new QhyResumeRequest(owner.OwnerToken, owner.LeaseSeconds, AutomationActor),
            owner,
            cancellationToken);
    }

    public Task<QhyJobSnapshot> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var owner = RequireOwnerSession(id);
        return PostOwnedControlAsync(
            id,
            "cancel",
            new QhyOwnerControlRequest(owner.OwnerToken, AutomationActor),
            owner,
            cancellationToken);
    }

    public async Task<QhyJobSnapshot> TakeoverAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        var snapshot = await PostControlAsync(
            id,
            "takeover",
            new OperatorTakeoverRequest(true, SafeOperatorName(), reason),
            cancellationToken).ConfigureAwait(false);
        if (IsTerminal(snapshot.State)) ownerSessions.TryRemove(id, out _);
        return snapshot;
    }

    public Task<QhyJobSnapshot> RenewLeaseAsync(
        Guid id,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        var owner = RequireOwnerSession(id);
        return PostOwnedControlAsync(
            id,
            "lease/renew",
            new QhyLeaseRenewalRequest(owner.OwnerToken, leaseSeconds, AutomationActor),
            owner with { LeaseSeconds = leaseSeconds },
            cancellationToken);
    }

    public Task<QhyJobSnapshot?> RecoverAndCancelAcceptedStartAsync(
        AcquisitionJobRequest request,
        CancellationToken cancellationToken) =>
        RecoverAcceptedStartAsync(
            request.ObservationRunId,
            QhyJobKind.Acquisition,
            RequireClientRequestId(request.ClientRequestId),
            token => StartAcquisitionAsync(request, token),
            cancellationToken);

    public Task<QhyJobSnapshot?> RecoverAndCancelAcceptedStartAsync(
        PhotometryJobRequest request,
        CancellationToken cancellationToken) =>
        RecoverAcceptedStartAsync(
            request.ObservationRunId,
            QhyJobKind.Photometry,
            RequireClientRequestId(request.ClientRequestId),
            token => StartPhotometryAsync(request, token),
            cancellationToken);

    public void Dispose()
    {
        ownerSessions.Clear();
        http.Dispose();
    }

    private async Task<QhyJobSnapshot> PostJobAsync<T>(string path, T body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(path, body, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        QhyJobSnapshot snapshot;
        try
        {
            snapshot = await response.Content.ReadFromJsonAsync<QhyJobSnapshot>(cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException("QHY service returned an empty accepted-job response.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new QhyAmbiguousStartException("QHY accepted a start POST but its public job body could not be decoded.", ex);
        }

        var session = ParseOwnerSession(response, snapshot);
        if (IsTerminal(snapshot.State))
        {
            ownerSessions.TryRemove(snapshot.Id, out _);
        }
        else
        {
            ownerSessions[snapshot.Id] = session;
        }
        return snapshot;
    }

    private async Task<QhyJobSnapshot> PostOwnedControlAsync<T>(
        Guid id,
        string action,
        T body,
        QhyOwnerSession currentOwner,
        CancellationToken cancellationToken)
    {
        var snapshot = await PostControlAsync(id, action, body, cancellationToken).ConfigureAwait(false);
        if (IsTerminal(snapshot.State))
        {
            ownerSessions.TryRemove(id, out _);
        }
        else
        {
            ownerSessions[id] = currentOwner with
            {
                LeaseExpiresUtc = snapshot.LeaseExpiresUtc ?? currentOwner.LeaseExpiresUtc,
                LeaseSeconds = snapshot.ControlLeaseSeconds,
            };
        }
        return snapshot;
    }

    private async Task<QhyJobSnapshot> PostControlAsync<T>(
        Guid id,
        string action,
        T body,
        CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"/api/v1/jobs/{id:D}/{action}", body, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<QhyJobSnapshot>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"QHY service returned no state after {action}.");
    }

    private async Task<QhyJobSnapshot?> RecoverAcceptedStartAsync(
        string observationRunId,
        QhyJobKind kind,
        string clientRequestId,
        Func<CancellationToken, Task<QhyJobSnapshot>> repeatExactStart,
        CancellationToken cancellationToken)
    {
        var discovered = await FindJobAsync(observationRunId, kind, clientRequestId, cancellationToken).ConfigureAwait(false);
        if (discovered is null || IsTerminal(discovered.State)) return discovered;

        if (!HasOwnerSession(discovered.Id))
        {
            try
            {
                var reacquired = await repeatExactStart(cancellationToken).ConfigureAwait(false);
                if (reacquired.Id != discovered.Id)
                {
                    if (HasOwnerSession(reacquired.Id) && !IsTerminal(reacquired.State))
                    {
                        _ = await CancelAndConfirmAsync(reacquired.Id, cancellationToken).ConfigureAwait(false);
                    }
                    throw new InvalidOperationException(
                        $"Idempotent QHY recovery returned job {reacquired.Id:D}, expected {discovered.Id:D}.");
                }
            }
            catch (Exception reacquireFailure) when (reacquireFailure is not OperationCanceledException)
            {
                // The accepted job is known to exist but its private credential is
                // unavailable. A confirmed takeover is the only API operation that
                // can stop the orphan without guessing or exposing a token.
                try
                {
                    return await TakeoverAsync(
                        discovered.Id,
                        "Ambiguous idempotent start was accepted, but its owner token could not be recovered; forced safe release.",
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception takeoverFailure)
                {
                    throw new AggregateException(
                        "QHY accepted an idempotent start, but neither owner-token recovery nor forced safe release completed.",
                        reacquireFailure,
                        takeoverFailure);
                }
            }
        }

        return await CancelAndConfirmAsync(discovered.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<QhyJobSnapshot> CancelAndConfirmAsync(Guid id, CancellationToken cancellationToken)
    {
        var cancelling = await CancelAsync(id, cancellationToken).ConfigureAwait(false);
        if (IsTerminal(cancelling.State)) return cancelling;
        return await WaitForCheckedTerminalAsync(id, TimeSpan.FromSeconds(15), onPoll: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Exception?> TryRecoverAmbiguousStartAsync(
        AcquisitionJobRequest request,
        CancellationToken cancellationToken)
    {
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        recovery.CancelAfter(AmbiguousStartRecoveryTimeout);
        try
        {
            _ = await RecoverAndCancelAcceptedStartAsync(request, recovery.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private async Task<Exception?> TryRecoverAmbiguousStartAsync(
        PhotometryJobRequest request,
        CancellationToken cancellationToken)
    {
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        recovery.CancelAfter(AmbiguousStartRecoveryTimeout);
        try
        {
            _ = await RecoverAndCancelAcceptedStartAsync(request, recovery.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private async Task RecoverCancelledAmbiguousStartBestEffortAsync(
        AcquisitionJobRequest request,
        CancellationToken cancellationToken)
    {
        _ = await TryRecoverAmbiguousStartAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverCancelledAmbiguousStartBestEffortAsync(
        PhotometryJobRequest request,
        CancellationToken cancellationToken)
    {
        _ = await TryRecoverAmbiguousStartAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private QhyOwnerSession RequireOwnerSession(Guid id) =>
        ownerSessions.TryGetValue(id, out var owner)
            ? owner
            : throw new InvalidOperationException(
                $"QHY job {id:D} has no in-memory owner token. Public job lookup cannot recover control; no mutating request was sent.");

    private static QhyOwnerSession ParseOwnerSession(HttpResponseMessage response, QhyJobSnapshot snapshot)
    {
        if (!response.Headers.TryGetValues(QhyControlProtocol.OwnerTokenHeaderName, out var tokenValues))
        {
            throw new QhyAmbiguousStartException($"QHY accepted job {snapshot.Id:D} without the private owner-token response header.");
        }
        var tokens = tokenValues.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim()).ToArray();
        if (tokens.Length != 1 || tokens[0].Length < 40)
        {
            throw new QhyAmbiguousStartException($"QHY accepted job {snapshot.Id:D} with a malformed private owner-token response header.");
        }
        if (!response.Headers.TryGetValues(QhyControlProtocol.LeaseExpiresUtcHeaderName, out var expiryValues))
        {
            throw new QhyAmbiguousStartException($"QHY accepted job {snapshot.Id:D} without its lease-expiry response header.");
        }
        var expiryTexts = expiryValues.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (expiryTexts.Length != 1 ||
            !DateTimeOffset.TryParse(
                expiryTexts[0],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var headerExpiry) ||
            snapshot.LeaseExpiresUtc is not { } bodyExpiry ||
            Math.Abs((headerExpiry - bodyExpiry).TotalSeconds) > 2)
        {
            throw new QhyAmbiguousStartException($"QHY accepted job {snapshot.Id:D} with inconsistent lease-expiry evidence.");
        }
        if (snapshot.ControlLeaseSeconds is < 15 or > 3600)
        {
            throw new QhyAmbiguousStartException($"QHY accepted job {snapshot.Id:D} with invalid lease duration metadata.");
        }
        return new QhyOwnerSession(tokens[0], headerExpiry, snapshot.ControlLeaseSeconds);
    }

    private void ForgetOwnerIfTerminal(QhyJobSnapshot snapshot)
    {
        if (IsTerminal(snapshot.State)) ownerSessions.TryRemove(snapshot.Id, out _);
    }

    private static bool IsAmbiguousStartFailure(Exception ex) => ex switch
    {
        QhyAmbiguousStartException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout } => true,
        JsonException => true,
        IOException => true,
        TaskCanceledException => true,
        _ => false,
    };

    private static bool IsTerminal(QhyJobState state) => state is
        QhyJobState.Completed or QhyJobState.Cancelled or QhyJobState.Faulted or QhyJobState.TakenOver;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"QHY service returned {(int)response.StatusCode}: {detail}",
            inner: null,
            response.StatusCode);
    }

    private static void ValidateIdempotencyKey(string? clientRequestId, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(clientRequestId))
        {
            throw new ArgumentException("A stable QHY ClientRequestId is required.", argumentName);
        }
    }

    private static string RequireClientRequestId(string? clientRequestId) =>
        !string.IsNullOrWhiteSpace(clientRequestId)
            ? clientRequestId
            : throw new ArgumentException("A stable QHY ClientRequestId is required.", nameof(clientRequestId));

    private static string SafeOperatorName() =>
        string.IsNullOrWhiteSpace(Environment.UserName) ? "UVEX-ADV" : Environment.UserName;
}

internal sealed record QhyOwnerSession(
    string OwnerToken,
    DateTimeOffset LeaseExpiresUtc,
    int LeaseSeconds);

internal sealed class QhyAmbiguousStartException : InvalidOperationException
{
    public QhyAmbiguousStartException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
