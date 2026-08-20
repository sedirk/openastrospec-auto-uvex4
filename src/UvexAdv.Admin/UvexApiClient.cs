using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using UvexAdv.Core;

namespace UvexAdv.Admin;

internal sealed record LeaseResponse(string Token, string Owner, DateTimeOffset ExpiresUtc);
internal sealed record OperationResponse(Guid Id, string Kind, string State, DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc, string? Error);

internal interface IUvexApiClient : IDisposable
{
    Task<UvexDeviceStatus?> GetStatusAsync(CancellationToken cancellationToken);
    Task<LeaseResponse> AcquireLeaseAsync(CancellationToken cancellationToken);
    Task ReleaseLeaseAsync(string token, CancellationToken cancellationToken);
    Task<LeaseResponse> RenewLeaseAsync(string token, CancellationToken cancellationToken);
    Task<OperationResponse> PostOperationAsync(string path, object? body, string? leaseToken, CancellationToken cancellationToken);
    Task<OperationResponse?> GetOperationAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class UvexApiClient(Uri baseAddress) : IUvexApiClient
{
    private readonly HttpClient http = new() { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };

    public Task<UvexDeviceStatus?> GetStatusAsync(CancellationToken cancellationToken) =>
        http.GetFromJsonAsync<UvexDeviceStatus>("/api/v1/device", cancellationToken);

    public async Task<LeaseResponse> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("/api/v1/leases", new { owner = "UVEX-ADV Admin", ttlSeconds = 60 }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<LeaseResponse>(cancellationToken: cancellationToken))!;
    }

    public async Task ReleaseLeaseAsync(string token, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync($"/api/v1/leases/{token}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<LeaseResponse> RenewLeaseAsync(string token, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync($"/api/v1/leases/{token}/renew", new { ttlSeconds = 60 }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<LeaseResponse>(cancellationToken: cancellationToken))!;
    }

    public Task<OperationResponse> PostOperationAsync(string path, object? body, string? leaseToken, CancellationToken cancellationToken) =>
        SendOperationAsync(path, body, leaseToken, cancellationToken);

    public async Task<OperationResponse?> GetOperationAsync(Guid id, CancellationToken cancellationToken) =>
        await http.GetFromJsonAsync<OperationResponse>($"/api/v1/operations/{id}", cancellationToken);

    public void Dispose() => http.Dispose();

    private async Task<OperationResponse> SendOperationAsync(string path, object? body, string? leaseToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? JsonContent.Create(new { }) : JsonContent.Create(body),
        };
        if (!string.IsNullOrWhiteSpace(leaseToken))
        {
            request.Headers.Add("X-Uvex-Lease", leaseToken);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<OperationResponse>(cancellationToken: cancellationToken))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"UVEX service returned {(int)response.StatusCode}: {detail}");
        }
    }
}
