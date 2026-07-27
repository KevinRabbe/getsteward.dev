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
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var visible = await presence.ListVisibleAsync(
            caller.Identity,
            new WorldId(worldId),
            cancellationToken);
        if (visible is null)
        {
            return Results.NotFound(new PlayerPresenceResponse(
                "NotFound",
                Retryable: false));
        }

        return Results.Ok(new PlayerPresenceResponse(
            "PlayerPresence",
            Retryable: false,
            Data: visible
                .Select(static item => new PlayerPresenceData(
                    item.Player.Provider,
                    item.Player.ExternalId))
                .ToArray()));
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

    public sealed record PlayerPresenceResponse(
        string Code,
        bool Retryable,
        IReadOnlyList<PlayerPresenceData>? Data = null);
}
