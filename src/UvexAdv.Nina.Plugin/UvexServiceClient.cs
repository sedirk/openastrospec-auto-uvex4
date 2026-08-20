using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using UvexAdv.Core;

namespace UvexAdv.Nina.Plugin;

internal sealed record ServiceLease(string Token, string Owner, DateTimeOffset ExpiresUtc);
internal sealed record ServiceOperation(Guid Id, string Kind, string State, DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc, string? Error);

internal sealed class UvexServiceClient : IDisposable
{
    private static readonly TimeSpan SlitIlluminationOperationTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient http;

    public UvexServiceClient(string serviceUrl)
        : this(serviceUrl, handler: null)
    {
    }

    internal UvexServiceClient(string serviceUrl, HttpMessageHandler? handler)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        http.BaseAddress = new Uri(serviceUrl, UriKind.Absolute);
        http.Timeout = TimeSpan.FromSeconds(10);
    }

    public Task<UvexDeviceStatus?> GetStatusAsync(CancellationToken cancellationToken) =>
        http.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device", cancellationToken);

    public async Task<UvexLeaseSession> AcquireLeaseAsync(string owner, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("/api/v1/leases", new { owner, ttlSeconds = 60 }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var lease = await response.Content.ReadFromJsonAsync<ServiceLease>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("UVEX service returned an empty lease response.");
        return new UvexLeaseSession(this, lease);
    }

    public Task<ServiceOperation> MoveFocusAsync(int deltaSteps, string leaseToken, CancellationToken cancellationToken) =>
        PostOperationAsync("/api/v1/focus/move", new { deltaSteps }, leaseToken, cancellationToken);
    public Task<ServiceOperation> MoveGratingAsync(int deltaSteps, string leaseToken, CancellationToken cancellationToken) =>
        PostOperationAsync("/api/v1/grating/move", new { deltaSteps }, leaseToken, cancellationToken);
    public Task<ServiceOperation> SelectSlitAsync(int position, string leaseToken, CancellationToken cancellationToken) =>
        PostOperationAsync("/api/v1/slit/select", new { position }, leaseToken, cancellationToken);
    public Task<ServiceOperation> GotoWavelengthAsync(double wavelengthNm, string leaseToken, CancellationToken cancellationToken) =>
        PostOperationAsync("/api/v1/grating/wavelength", new { wavelengthNm }, leaseToken, cancellationToken);
    public Task<ServiceOperation> HomeGratingAsync(string leaseToken, CancellationToken cancellationToken) =>
        PostOperationAsync("/api/v1/grating/home", new { }, leaseToken, cancellationToken);
    public Task<ServiceOperation> EnterMaintenanceAsync(string leaseToken, CancellationToken cancellationToken) =>
        PostOperationAsync("/api/v1/device/maintenance/enter", new { }, leaseToken, cancellationToken);
    public Task<ServiceOperation> ExitMaintenanceAsync(string leaseToken, CancellationToken cancellationToken) =>
        PostOperationAsync("/api/v1/device/maintenance/exit", new { }, leaseToken, cancellationToken);

    private async Task<UvexDeviceStatus> SetSlitIlluminationAsync(
        bool enabled,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SlitIlluminationOperationTimeout);
        ServiceOperation operation;
        try
        {
            operation = await PostOperationAsync(
                "/api/v1/slit/illumination",
                new { enabled },
                leaseToken,
                deadline.Token).ConfigureAwait(false);
            await WaitForOperationAsync(operation, deadline.Token).ConfigureAwait(false);
            var status = await GetStatusAsync(deadline.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "UVEX service returned no device state after the slit-illumination operation completed.");
            var expected = enabled ? UvexOutputState.On : UvexOutputState.Off;
            if (status.SlitIlluminationLedState != expected)
            {
                throw new InvalidOperationException(
                    $"UVEX slit-illumination operation succeeded, but readback is {status.SlitIlluminationLedState}; expected {expected}.");
            }
            if (status.SlitIlluminationLedCommandedUtc is not { } commandedUtc ||
                commandedUtc < operation.StartedUtc)
            {
                throw new InvalidOperationException(
                    "UVEX slit-illumination state did not include a current command timestamp after the completed operation.");
            }

            return status;
        }
        catch (OperationCanceledException ex) when (
            deadline.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"UVEX slit-illumination operation/readback did not complete within {SlitIlluminationOperationTimeout.TotalSeconds:F0} seconds.",
                ex);
        }
    }

    public async Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status?.ConnectionState == DeviceConnectionState.Ready && status.PositionKnown)
            {
                return;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("UVEX service did not become ready before the timeout.");
    }

    public async Task WaitForOperationAsync(ServiceOperation operation, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            var current = await http.GetFromJsonAsync<ServiceOperation>($"/api/v1/operations/{operation.Id}", cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("UVEX service lost the submitted operation.");
            switch (current.State)
            {
                case "Succeeded": return;
                case "Failed": throw new InvalidOperationException(current.Error ?? $"UVEX operation {current.Kind} failed.");
                case "Cancelled": throw new OperationCanceledException($"UVEX operation {current.Kind} was cancelled.");
            }
        }
    }

    public void Dispose() => http.Dispose();

    private async Task<ServiceOperation> PostOperationAsync(string path, object body, string leaseToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Uvex-Lease", leaseToken);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ServiceOperation>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("UVEX service returned an empty operation response.");
    }

    private async Task<ServiceLease> RenewAsync(string token, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"/api/v1/leases/{token}/renew", new { ttlSeconds = 60 }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ServiceLease>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("UVEX service returned an empty lease renewal response.");
    }

    private async Task ReleaseAsync(string token)
    {
        using var response = await http.DeleteAsync($"/api/v1/leases/{token}").ConfigureAwait(false);
        await EnsureSuccessAsync(response, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    internal sealed class UvexLeaseSession : IAsyncDisposable
    {
        private readonly UvexServiceClient owner;
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task renewal;

        public UvexLeaseSession(UvexServiceClient owner, ServiceLease lease)
        {
            this.owner = owner;
            Token = lease.Token;
            renewal = RenewLoopAsync();
        }

        public string Token { get; }

        /// <summary>
        /// Commands the positioning/slit-illumination LED under this live lease,
        /// waits for the service operation to finish, and verifies the service's
        /// post-command state. The UVEX protocol has no independent electrical
        /// state query; this is therefore a command-completion/readback proof,
        /// not a photometric proof that the LED illuminated the detector.
        /// </summary>
        public Task<UvexDeviceStatus> SetSlitIlluminationAsync(
            bool enabled,
            CancellationToken cancellationToken) =>
            owner.SetSlitIlluminationAsync(enabled, Token, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            Exception? renewalFailure = null;
            try { await renewal.ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex) { renewalFailure = ex; }

            Exception? releaseFailure = null;
            try { await owner.ReleaseAsync(Token).ConfigureAwait(false); }
            catch (Exception ex) { releaseFailure = ex; }
            finally { lifetime.Dispose(); }

            if (renewalFailure is not null || releaseFailure is not null)
            {
                throw new InvalidOperationException(
                    "UVEX control lease did not shut down cleanly.",
                    releaseFailure ?? renewalFailure);
            }
        }

        private async Task RenewLoopAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), lifetime.Token).ConfigureAwait(false);
                _ = await owner.RenewAsync(Token, lifetime.Token).ConfigureAwait(false);
            }
        }
    }
}
