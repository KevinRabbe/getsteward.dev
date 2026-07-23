using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Remote;

public enum RemoteWorldMemberStatus
{
    Active,
    RevocationPending
}

public sealed record StewardRemoteWorldMember(
    WorldId WorldId,
    StewardRemoteIdentity Identity,
    RemoteWorldMemberStatus Status,
    DateTimeOffset AddedAt);

public enum RemoteInvitationStatus
{
    Pending,
    Accepted,
    Declined
}

public sealed record StewardRemoteWorldInvitation(
    Guid InvitationId,
    WorldId WorldId,
    StewardRemoteIdentity InvitedIdentity,
    StewardRemoteIdentity InvitedBy,
    RemoteInvitationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RespondedAt);

public enum RemoteMemberRevocationStatus
{
    Revoked,
    RevocationPending
}

/// <summary>
/// Provider-neutral transport client for Steward's deliberately flat World access model. It exposes
/// membership, invitations, manager transfer, and leave/revoke actions without creating roles,
/// groups, gameplay priority, or a second social system.
/// </summary>
public sealed class StewardWorldAccessClient
{
    private readonly HttpClient _apiClient;
    private readonly IStewardAccessTokenProvider _accessTokens;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public StewardWorldAccessClient(
        HttpClient apiClient,
        IStewardAccessTokenProvider accessTokens)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(accessTokens);
        if (apiClient.BaseAddress is null)
        {
            throw new ArgumentException("Steward API HttpClient requires a BaseAddress.", nameof(apiClient));
        }

        _apiClient = apiClient;
        _accessTokens = accessTokens;
    }

    public async Task<IReadOnlyList<StewardRemoteWorldMember>> ListMembersAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Get,
            $"api/v1/worlds/{worldId.Value:D}/members",
            cancellationToken);
        var response = await SendAsync(request, cancellationToken);
        if (!string.Equals(response.Code, "WorldMembersFound", StringComparison.Ordinal))
        {
            throw CreateUnexpectedResponse(response);
        }

        return DeserializeRequiredData<MemberDto[]>(response)
            .Select(static member => member.ToDomain())
            .ToArray();
    }

    public async Task<IReadOnlyList<StewardRemoteWorldInvitation>> ListPendingInvitationsAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Get,
            "api/v1/invitations",
            cancellationToken);
        var response = await SendAsync(request, cancellationToken);
        if (!string.Equals(response.Code, "PendingInvitationsFound", StringComparison.Ordinal))
        {
            throw CreateUnexpectedResponse(response);
        }

        return DeserializeRequiredData<InvitationDto[]>(response)
            .Select(static invitation => invitation.ToDomain())
            .ToArray();
    }

    public async Task<StewardRemoteWorldInvitation> InviteAsync(
        WorldId worldId,
        string provider,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ValidateIdentity(provider, externalId);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            $"api/v1/worlds/{worldId.Value:D}/invitations",
            cancellationToken);
        request.Content = JsonContent.Create(new IdentityRequest(provider, externalId));
        var response = await SendAsync(request, cancellationToken);
        if (!string.Equals(response.Code, "InvitationCreated", StringComparison.Ordinal))
        {
            throw CreateUnexpectedResponse(response);
        }

        return DeserializeRequiredData<InvitationDto>(response).ToDomain();
    }

    public Task AcceptInvitationAsync(
        Guid invitationId,
        CancellationToken cancellationToken = default)
        => RespondToInvitationAsync(invitationId, accept: true, cancellationToken);

    public Task DeclineInvitationAsync(
        Guid invitationId,
        CancellationToken cancellationToken = default)
        => RespondToInvitationAsync(invitationId, accept: false, cancellationToken);

    public async Task<RemoteMemberRevocationStatus> RevokeMemberAsync(
        WorldId worldId,
        string provider,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ValidateIdentity(provider, externalId);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            $"api/v1/worlds/{worldId.Value:D}/members/revoke",
            cancellationToken);
        request.Content = JsonContent.Create(new IdentityRequest(provider, externalId));
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "MemberRevoked" => RemoteMemberRevocationStatus.Revoked,
            "MemberRevocationPending" => RemoteMemberRevocationStatus.RevocationPending,
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    public async Task LeaveWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            $"api/v1/worlds/{worldId.Value:D}/leave",
            cancellationToken);
        var response = await SendAsync(request, cancellationToken);
        if (!string.Equals(response.Code, "WorldLeft", StringComparison.Ordinal))
        {
            throw CreateUnexpectedResponse(response);
        }
    }

    public async Task TransferAccessManagerAsync(
        WorldId worldId,
        string provider,
        string externalId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ValidateIdentity(provider, externalId);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            $"api/v1/worlds/{worldId.Value:D}/access-manager",
            cancellationToken);
        request.Content = JsonContent.Create(new IdentityRequest(provider, externalId));
        var response = await SendAsync(request, cancellationToken);
        if (!string.Equals(response.Code, "AccessManagerTransferred", StringComparison.Ordinal))
        {
            throw CreateUnexpectedResponse(response);
        }
    }

    private async Task RespondToInvitationAsync(
        Guid invitationId,
        bool accept,
        CancellationToken cancellationToken)
    {
        if (invitationId == Guid.Empty)
        {
            throw new ArgumentException("Invitation ID is required.", nameof(invitationId));
        }

        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            $"api/v1/invitations/{invitationId:D}/{(accept ? "accept" : "decline")}",
            cancellationToken);
        var response = await SendAsync(request, cancellationToken);
        var expected = accept ? "InvitationAccepted" : "InvitationDeclined";
        if (!string.Equals(response.Code, expected, StringComparison.Ordinal))
        {
            throw CreateUnexpectedResponse(response);
        }
    }

    private async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (accessToken.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("Steward access token unexpectedly contains whitespace.");
        }

        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<ApiResponse> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await _apiClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        ApiResponse? envelope;
        try
        {
            envelope = await JsonSerializer.DeserializeAsync<ApiResponse>(stream, _jsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Steward returned malformed World-access JSON.", exception);
        }

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new InvalidDataException("Steward returned an invalid World-access response.");
        }

        return envelope with { StatusCode = response.StatusCode };
    }

    private T DeserializeRequiredData<T>(ApiResponse response)
    {
        if (response.Data is null || response.Data.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"Steward response '{response.Code}' omitted required data.");
        }

        try
        {
            return response.Data.Value.Deserialize<T>(_jsonOptions)
                   ?? throw new InvalidDataException(
                       $"Steward response '{response.Code}' returned null required data.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Steward returned invalid World-access data for '{response.Code}'.",
                exception);
        }
    }

    private static StewardRemoteApiException CreateUnexpectedResponse(ApiResponse response)
        => new(
            response.StatusCode,
            response.Code,
            response.Retryable || IsTransientStatus(response.StatusCode));

    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout ||
           statusCode == HttpStatusCode.TooManyRequests ||
           (int)statusCode >= 500;

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }

    private static void ValidateIdentity(string provider, string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
    }

    private sealed record IdentityRequest(string Provider, string ExternalId);

    private sealed record IdentityDto(string Provider, string ExternalId)
    {
        public StewardRemoteIdentity ToDomain() => new(Provider, ExternalId);
    }

    private sealed record MemberDto(
        Guid WorldId,
        IdentityDto Identity,
        string Status,
        DateTimeOffset AddedAt)
    {
        public StewardRemoteWorldMember ToDomain()
            => new(
                new WorldId(WorldId),
                Identity.ToDomain(),
                ParseMemberStatus(Status),
                AddedAt);
    }

    private sealed record InvitationDto(
        Guid InvitationId,
        Guid WorldId,
        IdentityDto InvitedIdentity,
        IdentityDto InvitedBy,
        string Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? RespondedAt)
    {
        public StewardRemoteWorldInvitation ToDomain()
            => new(
                InvitationId,
                new WorldId(WorldId),
                InvitedIdentity.ToDomain(),
                InvitedBy.ToDomain(),
                ParseInvitationStatus(Status),
                CreatedAt,
                RespondedAt);
    }

    private static RemoteWorldMemberStatus ParseMemberStatus(string status)
        => status switch
        {
            "Active" => RemoteWorldMemberStatus.Active,
            "RevocationPending" => RemoteWorldMemberStatus.RevocationPending,
            _ => throw new InvalidDataException($"Steward returned unknown World-member status '{status}'.")
        };

    private static RemoteInvitationStatus ParseInvitationStatus(string status)
        => status switch
        {
            "Pending" => RemoteInvitationStatus.Pending,
            "Accepted" => RemoteInvitationStatus.Accepted,
            "Declined" => RemoteInvitationStatus.Declined,
            _ => throw new InvalidDataException($"Steward returned unknown invitation status '{status}'.")
        };

    private sealed record ApiResponse(
        string Code,
        JsonElement? Data,
        bool Retryable)
    {
        public HttpStatusCode StatusCode { get; init; }
    }
}
