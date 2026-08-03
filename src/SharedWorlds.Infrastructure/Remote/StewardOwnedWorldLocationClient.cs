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
    private const int MaxReasonLength = 4096;
    private const int MaxWorldNameLength = 200;
    private const int MaxGameAdapterIdLength = 128;

    private static readonly JsonSerializerOptions ResponseJsonOptions =
        new(JsonSerializerDefaults.Web);

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

    public Task<OwnedWorldLocationTransportResponse> PublishCurrentLocationWithPresentationAsync(
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        string worldName,
        string gameAdapterId,
        RevisionId? expectedStateRevisionId = null,
        RevisionId? expectedEnvironmentRevisionId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureExpectedPair(expectedStateRevisionId, expectedEnvironmentRevisionId);
        ValidateBoundedText(worldName, nameof(worldName), MaxWorldNameLength);
        ValidateBoundedText(
            gameAdapterId,
            nameof(gameAdapterId),
            MaxGameAdapterIdLength);
        return SendAsync(
            HttpMethod.Put,
            $"api/v1/private-worlds/{worldId.Value:D}/location",
            new
            {
                stateRevisionId = stateRevisionId.Value,
                environmentRevisionId = environmentRevisionId.Value,
                expectedStateRevisionId = expectedStateRevisionId?.Value,
                expectedEnvironmentRevisionId = expectedEnvironmentRevisionId?.Value,
                worldName,
                gameAdapterId
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

    public async Task<StewardBringHereResolution> ResolveBringHereAvailabilityAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID must not be empty.", nameof(worldId));
        }

        var response = await ResolveBringHereAsync(worldId, cancellationToken);
        if (response.IsConflict ||
            !string.Equals(
                response.Code,
                "BringHereAvailability",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Steward returned unexpected Bring Here response '{response.Code}'.");
        }

        if (response.Data is not { } data || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The Bring Here availability response data is missing or malformed.");
        }

        BringHereDataWire wire;
        try
        {
            wire = data.Deserialize<BringHereDataWire>(ResponseJsonOptions)
                ?? throw new InvalidDataException(
                    "The Bring Here availability response data is missing.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The Bring Here availability response data could not be parsed.",
                exception);
        }

        if (!Enum.IsDefined(wire.Availability))
        {
            throw new InvalidDataException(
                $"The Bring Here availability value '{wire.Availability}' is unsupported.");
        }

        var reason = ValidateReason(wire.Reason);
        var source = wire.Source is null
            ? null
            : ValidateLocation(worldId, wire.Source, "source");
        var conflicts = (wire.ConflictingClaims ?? [])
            .Select((claim, index) =>
                ValidateLocation(worldId, claim, $"conflictingClaims[{index}]"))
            .ToArray();

        ValidateDistinctInstallations(source, conflicts);
        ValidateAvailabilityShape(wire.Availability, source, conflicts);

        return new StewardBringHereResolution(
            wire.Availability,
            source,
            conflicts,
            reason);
    }

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

    private static StewardOwnedWorldLocation ValidateLocation(
        WorldId requestedWorldId,
        OwnedWorldLocationWire wire,
        string path)
    {
        if (wire.WorldId == Guid.Empty || wire.WorldId != requestedWorldId.Value)
        {
            throw new InvalidDataException(
                $"Bring Here {path} does not reference the requested World.");
        }

        OwnedWorldLocationPublicationState.ValidateInstallationId(wire.InstallationId);
        if (wire.StateRevisionId == Guid.Empty || wire.EnvironmentRevisionId == Guid.Empty)
        {
            throw new InvalidDataException(
                $"Bring Here {path} requires non-empty state and environment revision IDs.");
        }

        if (wire.ObservedAt == default)
        {
            throw new InvalidDataException(
                $"Bring Here {path} requires an observation timestamp.");
        }

        var presentation = ValidatePresentation(
            wire.WorldName,
            wire.GameAdapterId,
            path);
        return new StewardOwnedWorldLocation(
            requestedWorldId,
            wire.InstallationId,
            new RevisionId(wire.StateRevisionId),
            new RevisionId(wire.EnvironmentRevisionId),
            wire.ObservedAt,
            presentation);
    }

    private static StewardOwnedWorldPresentation? ValidatePresentation(
        string? worldName,
        string? gameAdapterId,
        string path)
    {
        if ((worldName is null) != (gameAdapterId is null))
        {
            throw new InvalidDataException(
                $"Bring Here {path} must contain both World name and game adapter ID or neither.");
        }

        if (worldName is null)
        {
            return null;
        }

        ValidateBoundedText(worldName, $"{path}.worldName", MaxWorldNameLength);
        ValidateBoundedText(
            gameAdapterId!,
            $"{path}.gameAdapterId",
            MaxGameAdapterIdLength);
        return new StewardOwnedWorldPresentation(worldName, gameAdapterId!);
    }

    private static string ValidateReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidDataException(
                "The Bring Here availability response requires a reason.");
        }

        if (reason.Length > MaxReasonLength || reason.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"The Bring Here availability reason must be printable and at most {MaxReasonLength} characters.");
        }

        return reason;
    }

    private static void ValidateDistinctInstallations(
        StewardOwnedWorldLocation? source,
        IReadOnlyList<StewardOwnedWorldLocation> conflicts)
    {
        var installationIds = source is null
            ? conflicts.Select(claim => claim.InstallationId)
            : conflicts.Select(claim => claim.InstallationId)
                .Prepend(source.InstallationId);
        if (installationIds.Distinct(StringComparer.Ordinal).Count() !=
            installationIds.Count())
        {
            throw new InvalidDataException(
                "The Bring Here availability response contains duplicate installation claims.");
        }
    }

    private static void ValidateAvailabilityShape(
        BringHereAvailability availability,
        StewardOwnedWorldLocation? source,
        IReadOnlyList<StewardOwnedWorldLocation> conflicts)
    {
        switch (availability)
        {
            case BringHereAvailability.Unavailable:
                if (source is not null || conflicts.Count != 0)
                {
                    throw new InvalidDataException(
                        "Unavailable Bring Here responses cannot contain location claims.");
                }

                break;

            case BringHereAvailability.Available:
            case BringHereAvailability.AlreadyHere:
                if (source is null || conflicts.Count != 0)
                {
                    throw new InvalidDataException(
                        $"{availability} Bring Here responses require one source and no conflicting claims.");
                }

                break;

            case BringHereAvailability.Conflict:
                if (source is not null || conflicts.Count < 2)
                {
                    throw new InvalidDataException(
                        "Conflict Bring Here responses require at least two conflicting claims and no selected source.");
                }

                var distinctHeads = conflicts
                    .Select(claim => new
                    {
                        claim.StateRevisionId,
                        claim.EnvironmentRevisionId
                    })
                    .Distinct()
                    .Count();
                if (distinctHeads < 2)
                {
                    throw new InvalidDataException(
                        "Conflict Bring Here responses must contain divergent state/environment heads.");
                }

                break;

            default:
                throw new InvalidDataException(
                    $"Unsupported Bring Here availability '{availability}'.");
        }
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

    private static void ValidateBoundedText(
        string value,
        string parameterName,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        if (value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value must not exceed {maximumLength} characters.");
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Control characters are not allowed.", parameterName);
        }
    }

    private sealed record BringHereDataWire(
        BringHereAvailability Availability,
        OwnedWorldLocationWire? Source,
        OwnedWorldLocationWire[]? ConflictingClaims,
        string? Reason);

    private sealed record OwnedWorldLocationWire(
        Guid WorldId,
        string InstallationId,
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        DateTimeOffset ObservedAt,
        string? WorldName,
        string? GameAdapterId);
}

public sealed record OwnedWorldLocationTransportResponse(
    HttpStatusCode StatusCode,
    string Code,
    bool Retryable,
    JsonElement? Data)
{
    public bool IsConflict => StatusCode == HttpStatusCode.Conflict;
}

public sealed record StewardOwnedWorldPresentation(
    string Name,
    string GameAdapterId);

public sealed record StewardOwnedWorldLocation(
    WorldId WorldId,
    string InstallationId,
    RevisionId StateRevisionId,
    RevisionId EnvironmentRevisionId,
    DateTimeOffset ObservedAt,
    StewardOwnedWorldPresentation? Presentation = null);

public sealed record StewardBringHereResolution(
    BringHereAvailability Availability,
    StewardOwnedWorldLocation? Source,
    IReadOnlyList<StewardOwnedWorldLocation> ConflictingClaims,
    string Reason);
