using System.Net;
using System.Net.Sockets;
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
        HttpContext httpContext,
        StewardSessionService sessions,
        SharedWorldHostPresenceService hostPresence,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(httpContext.Request, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        if (request.ReservationSessionId == Guid.Empty || request.ReservationGeneration <= 0)
        {
            return Results.BadRequest(new HostPresenceResponse(
                "InvalidReservationIdentity",
                Retryable: false));
        }

        try
        {
            // A managed host already reaches Steward through the authenticated HTTPS connection. When
            // the adapter does not know a public address, reuse that connection peer instead of adding
            // a second external "what is my IP" service. Direct deployments expose the raw peer; an
            // explicitly configured trusted proxy may normalize RemoteIpAddress before this endpoint.
            // Host presence itself never parses or trusts forwarding headers.
            var address = ResolvePublishAddress(
                request.State,
                request.Address,
                httpContext.Connection.RemoteIpAddress);
            var status = await hostPresence.PublishAsync(
                caller.Identity,
                caller.InstallationId,
                new WorldId(worldId),
                request.ReservationSessionId,
                request.ReservationGeneration,
                request.State,
                address,
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
            return StewardApiResults.AuthenticationRequired();
        }

        var presence = await hostPresence.GetVisibleAsync(
            caller.Identity,
            new WorldId(worldId),
            cancellationToken);
        if (presence is null)
        {
            return Results.NotFound(new HostPresenceResponse(
                "NotFound",
                Retryable: false));
        }

        // Readers only need joinability evidence. Reservation/session/installation identity remains
        // internal authority state and is deliberately not disclosed through this read endpoint.
        return Results.Ok(new HostPresenceResponse(
            "HostPresence",
            Retryable: false,
            Data: new HostPresenceData(
                presence.State,
                presence.Address,
                presence.Port,
                presence.JoinToken)));
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
            return StewardApiResults.AuthenticationRequired();
        }

        if (sessionId == Guid.Empty || generation <= 0)
        {
            return Results.BadRequest(new HostPresenceResponse(
                "InvalidReservationIdentity",
                Retryable: false));
        }

        var deleted = await hostPresence.ClearAsync(
            caller.Identity,
            caller.InstallationId,
            new WorldId(worldId),
            sessionId,
            generation,
            cancellationToken);
        return deleted
            ? Results.Ok(new HostPresenceResponse("HostPresenceCleared", Retryable: false))
            : Results.NotFound(new HostPresenceResponse("NotFound", Retryable: false));
    }

    private static string? ResolvePublishAddress(
        SharedWorldHostPresenceState state,
        string? suppliedAddress,
        IPAddress? observedAddress)
    {
        if (!string.IsNullOrWhiteSpace(suppliedAddress) || state != SharedWorldHostPresenceState.Ready)
        {
            return suppliedAddress;
        }

        if (observedAddress is null)
        {
            return null;
        }

        if (observedAddress.IsIPv4MappedToIPv6)
        {
            observedAddress = observedAddress.MapToIPv4();
        }

        // The current Factorio direct-connect formatting is qualified only for IPv4/hostnames. Do not
        // invent IPv6 endpoint formatting until a real adapter/deployment path proves that requirement.
        return observedAddress.AddressFamily == AddressFamily.InterNetwork
            ? observedAddress.ToString()
            : null;
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

    public sealed record PublishHostPresenceRequest(
        Guid ReservationSessionId,
        long ReservationGeneration,
        SharedWorldHostPresenceState State,
        string? Address,
        int? Port,
        string? JoinToken);

    public sealed record HostPresenceData(
        SharedWorldHostPresenceState State,
        string? Address,
        int? Port,
        string? JoinToken);

    public sealed record HostPresenceResponse(
        string Code,
        bool Retryable,
        HostPresenceData? Data = null);
}
