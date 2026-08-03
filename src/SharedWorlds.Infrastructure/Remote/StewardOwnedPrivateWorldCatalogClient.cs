using System.Net.Http.Headers;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Remote;

public sealed class StewardOwnedPrivateWorldCatalogClient
{
    private const int MaxResponseBytes = 1024 * 1024;
    private const int MaxReasonLength = 4096;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly Func<CancellationToken, Task<string?>> _accessTokenProvider;

    public StewardOwnedPrivateWorldCatalogClient(
        HttpClient httpClient,
        Func<CancellationToken, Task<string?>> accessTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(accessTokenProvider);
        if (httpClient.BaseAddress is null ||
            !string.Equals(
                httpClient.BaseAddress.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Private World catalog access requires an HTTPS backend base address.",
                nameof(httpClient));
        }

        _httpClient = httpClient;
        _accessTokenProvider = accessTokenProvider;
    }

    public async Task<IReadOnlyList<StewardOwnedPrivateWorldCatalogEntry>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var token = await _accessTokenProvider(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "No authenticated Safe World session is available.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "api/v1/private-worlds");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await ReadBoundedJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        var code = root.GetProperty("code").GetString();
        if (!string.Equals(code, "OwnedPrivateWorldCatalog", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Steward returned unexpected private World catalog response '{code}'.");
        }

        if (!root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The private World catalog response data is missing or malformed.");
        }

        CatalogDataWire wire;
        try
        {
            wire = data.Deserialize<CatalogDataWire>(JsonOptions)
                ?? throw new InvalidDataException(
                    "The private World catalog response data is missing.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The private World catalog response could not be parsed.",
                exception);
        }

        var worlds = wire.Worlds ?? [];
        if (worlds.Length > OwnedPrivateWorldCatalogService.MaximumWorlds)
        {
            throw new InvalidDataException(
                $"The private World catalog exceeded {OwnedPrivateWorldCatalogService.MaximumWorlds} entries.");
        }

        var entries = worlds.Select(ValidateEntry).ToArray();
        if (entries.Select(entry => entry.WorldId).Distinct().Count() != entries.Length)
        {
            throw new InvalidDataException(
                "The private World catalog contains duplicate World IDs.");
        }

        return entries;
    }

    private static StewardOwnedPrivateWorldCatalogEntry ValidateEntry(
        CatalogEntryWire wire)
    {
        if (wire.WorldId == Guid.Empty)
        {
            throw new InvalidDataException(
                "A private World catalog entry requires a non-empty World ID.");
        }

        ValidateText(
            wire.Name,
            "Private World catalog name",
            OwnedWorldPresentation.MaximumNameLength);
        if (!Enum.IsDefined(typeof(BringHereAvailability), wire.Availability))
        {
            throw new InvalidDataException(
                $"The private World catalog availability '{wire.Availability}' is unsupported.");
        }

        var availability = (BringHereAvailability)wire.Availability;
        var worldId = new WorldId(wire.WorldId);
        var source = wire.Source is null
            ? null
            : ValidateLocation(worldId, wire.Source, "source");
        var conflicts = (wire.ConflictingClaims ?? [])
            .Select((claim, index) =>
                ValidateLocation(worldId, claim, $"conflictingClaims[{index}]"))
            .ToArray();
        EnsureDistinctInstallations(source, conflicts);
        var reason = ValidateReason(wire.Reason);

        string? gameAdapterId = wire.GameAdapterId;
        if (gameAdapterId is not null)
        {
            ValidateText(
                gameAdapterId,
                "Private World catalog adapter ID",
                OwnedWorldPresentation.MaximumGameAdapterIdLength);
        }

        switch (availability)
        {
            case BringHereAvailability.Available:
            case BringHereAvailability.AlreadyHere:
                if (gameAdapterId is null || source is null || conflicts.Length != 0)
                {
                    throw new InvalidDataException(
                        $"{availability} catalog entries require an adapter, one source, and no conflicts.");
                }

                break;

            case BringHereAvailability.Conflict:
                if (source is not null || conflicts.Length < 2)
                {
                    throw new InvalidDataException(
                        "Conflict catalog entries require at least two claims and no selected source.");
                }

                if (gameAdapterId is not null)
                {
                    var heads = conflicts
                        .Select(claim => new
                        {
                            claim.StateRevisionId,
                            claim.EnvironmentRevisionId
                        })
                        .Distinct()
                        .Count();
                    if (heads < 2)
                    {
                        throw new InvalidDataException(
                            "A head-conflict catalog entry must contain divergent state/environment heads.");
                    }
                }

                break;

            case BringHereAvailability.Unavailable:
                throw new InvalidDataException(
                    "Unavailable Worlds must not be included in the owner-private catalog.");

            default:
                throw new InvalidDataException(
                    $"Unsupported private World catalog availability '{availability}'.");
        }

        return new StewardOwnedPrivateWorldCatalogEntry(
            worldId,
            wire.Name,
            gameAdapterId,
            availability,
            source,
            conflicts,
            reason);
    }

    private static StewardOwnedWorldLocation ValidateLocation(
        WorldId expectedWorldId,
        LocationWire wire,
        string path)
    {
        if (wire.WorldId != expectedWorldId.Value)
        {
            throw new InvalidDataException(
                $"Private World catalog {path} references a different World.");
        }

        OwnedWorldLocationPublicationState.ValidateInstallationId(wire.InstallationId);
        if (wire.StateRevisionId == Guid.Empty || wire.EnvironmentRevisionId == Guid.Empty)
        {
            throw new InvalidDataException(
                $"Private World catalog {path} requires exact non-empty revision IDs.");
        }

        if (wire.ObservedAt == default)
        {
            throw new InvalidDataException(
                $"Private World catalog {path} requires an observation timestamp.");
        }

        StewardOwnedWorldPresentation? presentation = null;
        if ((wire.WorldName is null) != (wire.GameAdapterId is null))
        {
            throw new InvalidDataException(
                $"Private World catalog {path} contains partial presentation metadata.");
        }

        if (wire.WorldName is not null)
        {
            ValidateText(
                wire.WorldName,
                $"Private World catalog {path} name",
                OwnedWorldPresentation.MaximumNameLength);
            ValidateText(
                wire.GameAdapterId!,
                $"Private World catalog {path} adapter ID",
                OwnedWorldPresentation.MaximumGameAdapterIdLength);
            presentation = new StewardOwnedWorldPresentation(
                wire.WorldName,
                wire.GameAdapterId!);
        }

        return new StewardOwnedWorldLocation(
            expectedWorldId,
            wire.InstallationId,
            new RevisionId(wire.StateRevisionId),
            new RevisionId(wire.EnvironmentRevisionId),
            wire.ObservedAt,
            presentation);
    }

    private static void EnsureDistinctInstallations(
        StewardOwnedWorldLocation? source,
        IReadOnlyList<StewardOwnedWorldLocation> conflicts)
    {
        var installationIds = source is null
            ? conflicts.Select(claim => claim.InstallationId)
            : conflicts.Select(claim => claim.InstallationId)
                .Prepend(source.InstallationId);
        var values = installationIds.ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new InvalidDataException(
                "The private World catalog contains duplicate installation claims.");
        }
    }

    private static string ValidateReason(string? reason)
    {
        ValidateText(reason, "Private World catalog reason", MaxReasonLength);
        return reason!;
    }

    private static void ValidateText(
        string? value,
        string name,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{name} is required.");
        }

        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"{name} must be printable and at most {maximumLength} characters.");
        }
    }

    private static async Task<JsonDocument> ReadBoundedJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException(
                "The private World catalog response exceeded the allowed size.");
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
                throw new InvalidDataException(
                    "The private World catalog response exceeded the allowed size.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
    }

    private sealed record CatalogDataWire(CatalogEntryWire[]? Worlds);

    private sealed record CatalogEntryWire(
        Guid WorldId,
        string Name,
        string? GameAdapterId,
        int Availability,
        LocationWire? Source,
        LocationWire[]? ConflictingClaims,
        string? Reason);

    private sealed record LocationWire(
        Guid WorldId,
        string InstallationId,
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        DateTimeOffset ObservedAt,
        string? WorldName,
        string? GameAdapterId);
}

public sealed record StewardOwnedPrivateWorldCatalogEntry(
    WorldId WorldId,
    string Name,
    string? GameAdapterId,
    BringHereAvailability Availability,
    StewardOwnedWorldLocation? Source,
    IReadOnlyList<StewardOwnedWorldLocation> ConflictingClaims,
    string Reason);
