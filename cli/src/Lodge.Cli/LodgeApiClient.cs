using System.Net.Http.Json;
using System.Text.Json;

namespace Lodge.Cli;

/// <summary>Thin HTTP client over Lodge's API — the CLI is a peer of the UI, not a wrapper
/// around the server's own code; both talk to the exact same endpoints.</summary>
public sealed class LodgeApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;

    public LodgeApiClient(string serverUrl, string? token)
    {
        _http = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/") };
        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<LoginResponseDto> LoginAsync(CancellationToken ct)
    {
        var response = await _http.PostAsync("api/v1/auth/login", content: null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<LoginResponseDto>(JsonOptions, ct))!;
    }

    public async Task<AuthConfigDto> GetAuthConfigAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("api/v1/auth/config", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AuthConfigDto>(JsonOptions, ct))!;
    }

    public async Task<ServiceTokenDto> CreateServiceTokenAsync(
        string displayName, IReadOnlyList<string> scopes, int? ttlMinutes, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("api/v1/tokens",
            new { display_name = displayName, scopes, ttl_minutes = ttlMinutes }, JsonOptions, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ServiceTokenDto>(JsonOptions, ct))!;
    }

    public async Task<IReadOnlyList<ApiTokenSummaryDto>> ListTokensAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("api/v1/tokens", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<ApiTokenSummaryDto>>(JsonOptions, ct))!;
    }

    public async Task RevokeTokenAsync(Guid id, CancellationToken ct)
    {
        var response = await _http.DeleteAsync($"api/v1/tokens/{id}", ct);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            throw new LodgeApiException("Token not found.");
        }
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<CycleSummaryDto> ReconcileAsync(CancellationToken ct)
    {
        var response = await _http.PostAsync("api/v1/reconcile", content: null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<CycleSummaryDto>(JsonOptions, ct))!;
    }

    public async Task<IReadOnlyList<KindDto>> GetKindsAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("api/v1/kinds", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<KindDto>>(JsonOptions, ct))!;
    }

    public async Task<IReadOnlyList<InstanceDto>> GetInstancesAsync(string kindCode, CancellationToken ct)
    {
        var response = await _http.GetAsync($"api/v1/kinds/{kindCode}/instances", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<InstanceDto>>(JsonOptions, ct))!;
    }

    public async Task<InstanceDetailDto> GetInstanceAsync(string kindCode, string instanceCode, CancellationToken ct)
    {
        var response = await _http.GetAsync($"api/v1/kinds/{kindCode}/instances/{instanceCode}", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions, ct))!;
    }

    public async Task<IReadOnlyList<ActionDto>> GetActionsAsync(string kindCode, string instanceCode, CancellationToken ct)
    {
        var response = await _http.GetAsync($"api/v1/kinds/{kindCode}/instances/{instanceCode}/actions", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<ActionDto>>(JsonOptions, ct))!;
    }

    public async Task<IReadOnlyList<AuditEventDto>> GetEventsAsync(string kindCode, string instanceCode, CancellationToken ct)
    {
        var response = await _http.GetAsync($"api/v1/kinds/{kindCode}/instances/{instanceCode}/events", ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<AuditEventDto>>(JsonOptions, ct))!;
    }

    public async Task<ActionExecutionResultDto> ConfirmActionAsync(
        string kindCode, string instanceCode, Guid actionId, string? actor, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync(
            $"api/v1/kinds/{kindCode}/instances/{instanceCode}/actions/{actionId}/confirm",
            new { actor }, JsonOptions, ct);
        return await ReadExecutionResultAsync(response, ct);
    }

    public async Task<ActionExecutionResultDto> InvalidateActionAsync(
        string kindCode, string instanceCode, Guid actionId, string? actor, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync(
            $"api/v1/kinds/{kindCode}/instances/{instanceCode}/actions/{actionId}/invalidate",
            new { actor }, JsonOptions, ct);
        return await ReadExecutionResultAsync(response, ct);
    }

    // Confirm/invalidate can legitimately come back 403 (RBAC denial) with a body worth
    // showing the user, not just an exception — read it either way and let the caller
    // decide how to report it.
    private async Task<ActionExecutionResultDto> ReadExecutionResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            throw new LodgeApiException("Action not found.");
        }
        if (!response.IsSuccessStatusCode && response.StatusCode is not System.Net.HttpStatusCode.Forbidden)
        {
            await EnsureSuccessAsync(response, ct);
        }
        return (await response.Content.ReadFromJsonAsync<ActionExecutionResultDto>(JsonOptions, ct))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new LodgeApiException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    public void Dispose() => _http.Dispose();
}

public sealed class LodgeApiException(string message) : Exception(message);
