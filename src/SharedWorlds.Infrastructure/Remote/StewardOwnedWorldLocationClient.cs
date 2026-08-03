using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Remote;

public sealed class StewardOwnedWorldLocationClient
{
    private const int MaxResponseBytes = 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly Func<CancellationToken, Task<string?>> _accessTokenProvider;

    public StewardOwnedWorldLocationClient(
        HttpClient httpClient,
        Func<CancellationToken, Task<string?>> accessTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(accessTokenProvider);
        if (httpClient.BaseAddress is null ||
            !string.Equals(httpClient.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Bring Here requires an HTTPS backend base address.", nameof(httpClient));
        }

        _httpClient = httpClient;
        _accessTokenProvider = accessTokenProvider;
    }

    public Task<OwnedWorldLocationTransportResponse> RegisterCurrentInstallationAsync(
        string displayName,
        CancellationToken cancellationToken = default)
        => SendAsync(
            HttpMethod.Put,
            "api/v1/installations/current",
            new { displayName },
            cancellationToken);

    public Task<OwnedWorldLocationTransportResponse> PublishCurrentLocationAsync(
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        RevisionId? expectedStateRevisionId = null,
        RevisionId? expectedEnvironmentRevisionId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureExpectedPair(expectedStateRevisionId, expectedEnvironmentRevisionId);
        return SendAsync(
            HttpMethod.Put,
            $"api/v1/private-worlds/{worldId.Value:D}/location",
            new
            {
                stateRevisionId = stateRevisionId.Value,
                environmentRevisionId = environmentRevisionId.Value,
                expectedStateRevisionId = expectedStateRevisionId?.Value,
                expectedEnvironmentRevisionId = expectedEnvironmentRevisionId?.Value
            },
            cancellationToken);
    }

    public Task<OwnedWorldLocationTransportResponse> RemoveCurrentLocationAsync(
        WorldId worldId,
        RevisionId expectedStateRevisionId,
        RevisionId expectedEnvironmentRevisionId,
        CancellationToken cancellationToken = default)
    {
        var path =
            $"api/v1/private-worlds/{worldId.Value:D}/location" +
            $"?expectedStateRevisionId={Uri.EscapeDataString(expectedStateRevisionId.Value.ToString("D"))}" +
            $"&expectedEnvironmentRevisionId={Uri.EscapeDataString(expectedEnvironmentRevisionId.Value.ToString("D"))}";
        return SendAsync(HttpMethod.Delete, path, body: null, cancellationToken);
    }

    public Task<OwnedWorldLocationTransportResponse> ResolveBringHereAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
        => SendAsync(
            HttpMethod.Get,
            $"api/v1/private-worlds/{worldId.Value:D}/bring-here",
            body: null,
            cancellationToken);

    private async Task<OwnedWorldLocationTransportResponse> SendAsync(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken)
    {
        var token = await _accessTokenProvider(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("No authenticated Safe World session is available.");
        }

        using var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var document = await ReadBoundedJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        var code = root.GetProperty("code").GetString()
            ?? throw new InvalidDataException("The backend response code is missing.");
        var retryable = root.TryGetProperty("retryable", out var retryableElement) &&
                        retryableElement.GetBoolean();
        var data = root.TryGetProperty("data", out var dataElement)
            ? dataElement.Clone()
            : (JsonElement?)null;

        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            response.EnsureSuccessStatusCode();
        }

        return new OwnedWorldLocationTransportResponse(
            response.StatusCode,
            code,
            retryable,
            data);
    }

    private static async Task<JsonDocument> ReadBoundedJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException("The backend response exceeded the allowed size.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException("The backend response exceeded the allowed size.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
    }

    private static void EnsureExpectedPair(
        RevisionId? expectedStateRevisionId,
        RevisionId? expectedEnvironmentRevisionId)
    {
        if (expectedStateRevisionId.HasValue != expectedEnvironmentRevisionId.HasValue)
        {
            throw new ArgumentException(
                "Expected state and environment revisions must both be supplied or both be absent.");
        }
    }
}

public sealed record OwnedWorldLocationTransportResponse(
    HttpStatusCode StatusCode,
    string Code,
    bool Retryable,
    JsonElement? Data)
{
    public bool IsConflict => StatusCode == HttpStatusCode.Conflict;
}
