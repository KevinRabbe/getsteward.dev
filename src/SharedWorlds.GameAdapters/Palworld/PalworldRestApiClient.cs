using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SharedWorlds.GameAdapters.Palworld;

internal sealed record PalworldServerInfo(
    string Version,
    string ServerName,
    string Description,
    string WorldGuid);

/// <summary>
/// Small adapter-owned client for Palworld's documented dedicated-server REST management surface.
/// It deliberately contains only the operations Steward needs for lifecycle evidence: readiness,
/// settings observation, explicit save, and graceful shutdown. The API is expected to remain private
/// to the host device/LAN.
/// </summary>
internal sealed class PalworldRestApiClient
{
    private readonly HttpClient _httpClient;
    private readonly AuthenticationHeaderValue _authorization;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public PalworldRestApiClient(
        HttpClient httpClient,
        string username,
        string password)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (httpClient.BaseAddress is null)
        {
            throw new ArgumentException("Palworld REST HttpClient requires a BaseAddress.", nameof(httpClient));
        }

        if (!httpClient.BaseAddress.IsAbsoluteUri ||
            httpClient.BaseAddress.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "Palworld REST BaseAddress must be an absolute HTTP or HTTPS URI.",
                nameof(httpClient));
        }

        _httpClient = httpClient;
        _authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
    }

    public async Task<PalworldServerInfo> GetInfoAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "v1/api/info");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "read Palworld server readiness", cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        InfoResponse? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<InfoResponse>(
                stream,
                _jsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Palworld returned malformed server-info JSON.", exception);
        }

        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.Version) ||
            string.IsNullOrWhiteSpace(payload.ServerName) ||
            string.IsNullOrWhiteSpace(payload.WorldGuid))
        {
            throw new InvalidDataException("Palworld returned incomplete server readiness information.");
        }

        return new PalworldServerInfo(
            payload.Version,
            payload.ServerName,
            payload.Description ?? string.Empty,
            payload.WorldGuid);
    }

    public async Task<JsonDocument> GetSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, "v1/api/settings");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "read Palworld server settings", cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Palworld returned malformed server-settings JSON.", exception);
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, "v1/api/save");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "save the Palworld World", cancellationToken);
    }

    public async Task ShutdownAsync(
        int waitSeconds,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (waitSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(waitSeconds));
        }

        ArgumentNullException.ThrowIfNull(message);
        using var request = CreateRequest(HttpMethod.Post, "v1/api/shutdown");
        request.Content = JsonContent.Create(new ShutdownRequest(waitSeconds, message));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "shut down the Palworld server", cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = _authorization;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = string.Empty;
        if (response.Content.Headers.ContentLength is not 0)
        {
            try
            {
                detail = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                detail = string.Empty;
            }
        }

        var status = $"HTTP {(int)response.StatusCode} ({response.StatusCode})";
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                $"Palworld refused administrative REST authentication while Steward tried to {operation} ({status}).");
        }

        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(detail)
                ? $"Palworld could not {operation}: {status}."
                : $"Palworld could not {operation}: {status}. {detail}",
            inner: null,
            response.StatusCode);
    }

    private sealed record InfoResponse(
        string Version,
        string ServerName,
        string? Description,
        string WorldGuid);

    private sealed record ShutdownRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("waittime")] int WaitSeconds,
        string Message);
}
