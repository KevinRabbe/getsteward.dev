using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

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
        api.MapPost(
            "/worlds/{worldId:guid}/revisions/{revisionId:guid}/environment",
            PublishEnvironmentRevisionAsync);

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
                EnvironmentRevisionMetadataDto.From(revision)));
    }

    private static async Task<IResult> PublishEnvironmentRevisionAsync(
        Guid worldId,
        Guid revisionId,
        PublishEnvironmentRevisionRequest request,
        HttpContext context,
        StewardSessionService sessions,
        SharedRevisionMetadataService revisions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var caller = await AuthenticateAsync(context, sessions, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        var result = await revisions.PublishEnvironmentManifestAsync(
            caller.Identity,
            new WorldId(worldId),
            new RevisionId(revisionId),
            request.Manifest,
            cancellationToken);
        return result switch
        {
            PublishEnvironmentManifestStatus.Published => Results.Created(
                $"/api/v1/worlds/{worldId:D}/revisions/{revisionId:D}/environment",
                new StewardApiResponse("EnvironmentRevisionPublished")),
            PublishEnvironmentManifestStatus.AlreadyPublished => Results.Ok(
                new StewardApiResponse("EnvironmentRevisionAlreadyPublished")),
            PublishEnvironmentManifestStatus.NotFoundOrUnauthorized =>
                StewardApiResults.NotFound("WorldNotFoundOrUnauthorized"),
            PublishEnvironmentManifestStatus.InvalidManifest =>
                StewardApiResults.Validation("InvalidEnvironmentManifest"),
            PublishEnvironmentManifestStatus.AdapterMismatch =>
                StewardApiResults.DomainConflict("AdapterMismatch"),
            PublishEnvironmentManifestStatus.Conflict =>
                StewardApiResults.DomainConflict("RevisionConflict"),
            _ => throw new InvalidOperationException("Unexpected environment-manifest publication result.")
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

public sealed record PublishEnvironmentRevisionRequest(EnvironmentManifest Manifest);

public sealed record EnvironmentRevisionMetadataDto(
    Guid RevisionId,
    string ArtifactReference,
    long? ByteSize,
    string? Sha256,
    DateTimeOffset PublishedAt,
    EnvironmentManifest? Manifest)
{
    public static EnvironmentRevisionMetadataDto From(SharedEnvironmentRevisionMetadata revision)
        => new(
            revision.RevisionId.Value,
            revision.ArtifactReference,
            revision.ByteSize,
            revision.Sha256,
            revision.PublishedAt,
            revision.Manifest);
}
