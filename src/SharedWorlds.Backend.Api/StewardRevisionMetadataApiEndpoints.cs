using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Api;

public static class StewardRevisionMetadataApiEndpoints
{
    public static IEndpointRouteBuilder MapStewardRevisionMetadataApiV1(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var api = endpoints.MapGroup("/api/v1");

        api.MapGet(
            "/worlds/{worldId:guid}/revisions/{revisionId:guid}/state",
            GetStateRevisionAsync);
        api.MapGet(
            "/worlds/{worldId:guid}/revisions/{revisionId:guid}/environment",
            GetEnvironmentRevisionAsync);

        return endpoints;
    }

    private static async Task<IResult> GetStateRevisionAsync(
        Guid worldId,
        Guid revisionId,
        HttpContext context,
        StewardSessionService sessions,
        SharedRevisionMetadataService revisions,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(context, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var revision = await revisions.GetStateRevisionMetadataAsync(
            caller.Identity,
            new WorldId(worldId),
            new RevisionId(revisionId),
            cancellationToken);
        return revision is null
            ? StewardApiResults.NotFound("RevisionNotFoundOrUnauthorized")
            : Results.Ok(new StewardApiResponse(
                "StateRevisionFound",
                SharedStateRevisionDto.From(revision)));
    }

    private static async Task<IResult> GetEnvironmentRevisionAsync(
        Guid worldId,
        Guid revisionId,
        HttpContext context,
        StewardSessionService sessions,
        SharedRevisionMetadataService revisions,
        CancellationToken cancellationToken)
    {
        var caller = await AuthenticateAsync(context, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var revision = await revisions.GetEnvironmentRevisionMetadataAsync(
            caller.Identity,
            new WorldId(worldId),
            new RevisionId(revisionId),
            cancellationToken);
        return revision is null
            ? StewardApiResults.NotFound("RevisionNotFoundOrUnauthorized")
            : Results.Ok(new StewardApiResponse(
                "EnvironmentRevisionFound",
                SharedEnvironmentRevisionDto.From(revision)));
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
