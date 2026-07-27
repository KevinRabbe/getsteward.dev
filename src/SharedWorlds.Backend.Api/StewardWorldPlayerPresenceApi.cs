using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Api;

public static class StewardWorldPlayerPresenceApi
{
    public static IEndpointRouteBuilder MapStewardWorldPlayerPresenceApiV1(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPut(
            "/api/v1/worlds/{worldId:guid}/player-presence",
            PublishAsync);
        endpoints.MapGet(
            "/api/v1/worlds/{worldId:guid}/player-presence",
            ListAsync);
        endpoints.MapDelete(
            "/api/v1/worlds/{worldId:guid}/player-presence",
            ClearAsync);

        return endpoints;
    }

    private static async Task<IResult> PublishAsync(
        Guid worldId,
        HttpRequest request,
        StewardSessionService sessions,
        SharedWorldPlayerPresenceService presence,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var status = await presence.PublishAsync(
            caller.Identity,
            caller.InstallationId,
            new WorldId(worldId),
            cancellationToken);
        return status switch
        {
            PublishSharedWorldPlayerPresenceStatus.Published => Results.Ok(
                new PlayerPresenceResponse("PlayerPresencePublished", Retryable: false)),
            PublishSharedWorldPlayerPresenceStatus.NotFoundOrUnauthorized => Results.NotFound(
                new PlayerPresenceResponse("NotFound", Retryable: false)),
            _ => throw new InvalidOperationException(
                $"Unhandled player presence publish status {status}.")
        };
    }

    private static async Task<IResult> ListAsync(
        Guid worldId,
        HttpRequest request,
        StewardSessionService sessions,
        SharedWorldPlayerPresenceService presence,
        SharedWorldHostPresenceService hostPresence,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var id = new WorldId(worldId);
        var visible = await presence.ListVisibleAsync(
            caller.Identity,
            id,
            cancellationToken);
        if (visible is null)
        {
            return Results.NotFound(new PlayerPresenceResponse(
                "NotFound",
                Retryable: false));
        }

        // Lobby Host truth is composed from the already-existing reservation-backed Host-presence
        // service. The Join endpoint remains unchanged and lobby readers receive no address/token,
        // reservation generation, session ID, or installation ID.
        var host = await hostPresence.GetVisibleAsync(
            caller.Identity,
            id,
            cancellationToken);

        return Results.Ok(new PlayerPresenceResponse(
            "PlayerPresence",
            Retryable: false,
            Data: new PlayerPresenceSnapshotData(
                visible
                    .Select(static item => new PlayerPresenceData(
                        item.Player.Provider,
                        item.Player.ExternalId))
                    .ToArray(),
                host is null
                    ? null
                    : new LobbyHostData(
                        host.Holder.Provider,
                        host.Holder.ExternalId,
                        host.State))));
    }

    private static async Task<IResult> ClearAsync(
        Guid worldId,
        HttpRequest request,
        StewardSessionService sessions,
        SharedWorldPlayerPresenceService presence,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var deleted = await presence.ClearAsync(
            caller.Identity,
            caller.InstallationId,
            new WorldId(worldId),
            cancellationToken);
        return deleted
            ? Results.Ok(new PlayerPresenceResponse("PlayerPresenceCleared", Retryable: false))
            : Results.NotFound(new PlayerPresenceResponse("NotFound", Retryable: false));
    }

    private static async Task<StewardAuthenticatedCaller?> AuthenticateAsync(
        HttpRequest request,
        StewardSessionService sessions,
        CancellationToken cancellationToken)
    {
        var authorization = request.Headers.Authorization.ToString();
        const string bearerPrefix = "Bearer ";
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var accessToken = authorization[bearerPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        return await sessions.ValidateAccessTokenAsync(accessToken, cancellationToken);
    }

    public sealed record PlayerPresenceData(
        string Provider,
        string ExternalId);

    public sealed record LobbyHostData(
        string Provider,
        string ExternalId,
        SharedWorldHostPresenceState State);

    public sealed record PlayerPresenceSnapshotData(
        IReadOnlyList<PlayerPresenceData> Players,
        LobbyHostData? Host);

    public sealed record PlayerPresenceResponse(
        string Code,
        bool Retryable,
        PlayerPresenceSnapshotData? Data = null);
}
