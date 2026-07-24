using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Api;

public static class StewardHostPresenceApi
{
    public static IEndpointRouteBuilder MapStewardHostPresenceApiV1(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPut(
            "/api/v1/worlds/{worldId:guid}/host-presence",
            PublishAsync);
        endpoints.MapGet(
            "/api/v1/worlds/{worldId:guid}/host-presence",
            GetAsync);
        endpoints.MapDelete(
            "/api/v1/worlds/{worldId:guid}/host-presence/{sessionId:guid}/{generation:long}",
            ClearAsync);

        return endpoints;
    }

    private static async Task<IResult> PublishAsync(
        Guid worldId,
        PublishHostPresenceRequest request,
        HttpRequest httpRequest,
        StewardSessionService sessions,
        SharedWorldHostPresenceService hostPresence,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(httpRequest, sessions, cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        if (request.ReservationSessionId == Guid.Empty || request.ReservationGeneration <= 0)
        {
            return Results.BadRequest(new HostPresenceResponse(
                "InvalidReservationIdentity",
                Retryable: false));
        }

        try
        {
            var status = await hostPresence.PublishAsync(
                ToIdentityRef(caller.Identity),
                caller.InstallationId,
                new WorldId(worldId),
                request.ReservationSessionId,
                request.ReservationGeneration,
                request.State,
                request.Address,
                request.Port,
                request.JoinToken,
                cancellationToken);

            return status switch
            {
                PublishSharedWorldHostPresenceStatus.Published => Results.Ok(
                    new HostPresenceResponse("HostPresencePublished", Retryable: false)),
                PublishSharedWorldHostPresenceStatus.ReservationMismatch => Results.Conflict(
                    new HostPresenceResponse("ReservationMismatch", Retryable: false)),
                PublishSharedWorldHostPresenceStatus.NotFoundOrUnauthorized => Results.NotFound(
                    new HostPresenceResponse("NotFound", Retryable: false)),
                _ => throw new InvalidOperationException(
                    $"Unhandled host presence publish status {status}.")
            };
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new HostPresenceResponse(
                "InvalidHostPresence",
                Retryable: false));
        }
    }

    private static async Task<IResult> GetAsync(
        Guid worldId,
        HttpRequest httpRequest,
        StewardSessionService sessions,
        SharedWorldHostPresenceService hostPresence,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(httpRequest, sessions, cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        var presence = await hostPresence.GetVisibleAsync(
            ToIdentityRef(caller.Identity),
            new WorldId(worldId),
            cancellationToken);
        if (presence is null)
        {
            return Results.NotFound(new HostPresenceResponse(
                "NotFound",
                Retryable: false));
        }

        return Results.Ok(new HostPresenceResponse(
            "HostPresence",
            Retryable: false,
            Data: new HostPresenceData(
                presence.WorldId.Value,
                presence.SessionId,
                presence.Generation,
                presence.InstallationId,
                presence.State,
                presence.Address,
                presence.Port,
                presence.JoinToken,
                presence.UpdatedAt)));
    }

    private static async Task<IResult> ClearAsync(
        Guid worldId,
        Guid sessionId,
        long generation,
        HttpRequest httpRequest,
        StewardSessionService sessions,
        SharedWorldHostPresenceService hostPresence,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(httpRequest, sessions, cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        if (sessionId == Guid.Empty || generation <= 0)
        {
            return Results.BadRequest(new HostPresenceResponse(
                "InvalidReservationIdentity",
                Retryable: false));
        }

        var deleted = await hostPresence.ClearAsync(
            ToIdentityRef(caller.Identity),
            new WorldId(worldId),
            sessionId,
            generation,
            cancellationToken);
        return deleted
            ? Results.Ok(new HostPresenceResponse("HostPresenceCleared", Retryable: false))
            : Results.NotFound(new HostPresenceResponse("NotFound", Retryable: false));
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

        return await sessions.ResolveAccessTokenAsync(accessToken, cancellationToken);
    }

    private static ExternalIdentityRef ToIdentityRef(VerifiedExternalIdentity identity)
        => new(identity.Provider, identity.ExternalId);

    public sealed record PublishHostPresenceRequest(
        Guid ReservationSessionId,
        long ReservationGeneration,
        SharedWorldHostPresenceState State,
        string? Address,
        int? Port,
        string? JoinToken);

    public sealed record HostPresenceData(
        Guid WorldId,
        Guid ReservationSessionId,
        long ReservationGeneration,
        string HostInstallationId,
        SharedWorldHostPresenceState State,
        string? Address,
        int? Port,
        string? JoinToken,
        DateTimeOffset UpdatedAt);

    public sealed record HostPresenceResponse(
        string Code,
        bool Retryable,
        HostPresenceData? Data = null);
}
