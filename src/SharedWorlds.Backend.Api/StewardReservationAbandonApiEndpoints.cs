using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Api;

public static class StewardReservationAbandonApiEndpoints
{
    public static IEndpointRouteBuilder MapStewardReservationAbandonApiV1(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(
            "/api/v1/worlds/{worldId:guid}/reservation/abandon",
            AbandonAsync);
        return endpoints;
    }

    private static async Task<IResult> AbandonAsync(
        Guid worldId,
        AbandonWorldReservationRequest request,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldReservationAbandonService abandon,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = await AuthenticateAsync(context, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var result = await abandon.AbandonAsync(
            caller.Identity,
            new WorldId(worldId),
            request.InstallationId,
            request.SessionId,
            request.Generation,
            cancellationToken);
        return result switch
        {
            AbandonSharedWorldReservationStatus.Abandoned =>
                Results.Ok(new StewardApiResponse("ReservationAbandoned")),
            AbandonSharedWorldReservationStatus.NoLongerCurrent =>
                Results.Ok(new StewardApiResponse("ReservationNoLongerCurrent")),
            _ => throw new InvalidOperationException("Unexpected reservation-abandon result.")
        };
    }

    private static async Task<StewardAuthenticatedCaller?> AuthenticateAsync(
        HttpContext context,
        StewardSessionService sessions,
        CancellationToken cancellationToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization[prefix.Length..].Trim();
        if (token.Length == 0 || token.Contains(' '))
        {
            return null;
        }

        return await sessions.ValidateAccessTokenAsync(token, cancellationToken);
    }
}

public sealed record AbandonWorldReservationRequest(
    string InstallationId,
    Guid SessionId,
    long Generation);
