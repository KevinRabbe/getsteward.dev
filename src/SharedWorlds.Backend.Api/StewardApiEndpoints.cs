using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Api;

public static class StewardApiEndpoints
{
    public static IEndpointRouteBuilder MapStewardApiV1(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var api = endpoints.MapGroup("/api/v1");

        api.MapPost("/auth/steam/session", AuthenticateSteamAsync);
        api.MapPost("/auth/refresh", RefreshSessionAsync);
        api.MapPost("/auth/revoke", RevokeSessionAsync);

        api.MapGet("/worlds", ListWorldsAsync);
        api.MapPost("/worlds", CreateWorldAsync);
        api.MapGet("/worlds/{worldId:guid}", GetWorldAsync);
        api.MapGet("/worlds/{worldId:guid}/current-revision", GetCurrentRevisionAsync);

        api.MapPost("/worlds/{worldId:guid}/transfers", BeginTransferAsync);
        api.MapGet("/transfers/{transferId:guid}", GetTransferProgressAsync);
        api.MapPost("/transfers/{transferId:guid}/parts/{partNumber:int}/authorization", AuthorizeTransferPartAsync);
        api.MapPost("/transfers/{transferId:guid}/finalize", FinalizeTransferAsync);
        api.MapPost(
            "/worlds/{worldId:guid}/revisions/{revisionId:guid}/{kind}/download-authorization",
            AuthorizeDownloadAsync);

        return endpoints;
    }

    private static async Task<IResult> AuthenticateSteamAsync(
        SteamSessionRequest request,
        SteamWebApiTicketVerifier verifier,
        StewardSessionService sessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var verification = await verifier.VerifyAsync(request.TicketHex, cancellationToken);
        if (verification.Status != ExternalIdentityTicketVerificationStatus.Verified ||
            verification.Identity is null)
        {
            return StewardApiResults.Problem(
                StatusCodes.Status401Unauthorized,
                "InvalidSteamTicket",
                "Steam authentication failed.");
        }

        var tokens = await sessions.CreateSessionAsync(
            verification.Identity,
            request.InstallationId,
            cancellationToken);

        return Results.Ok(new StewardApiResponse(
            "Authenticated",
            StewardSessionDto.From(tokens)));
    }

    private static async Task<IResult> RefreshSessionAsync(
        RefreshSessionRequest request,
        StewardSessionService sessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sessions.RefreshAsync(
            request.RefreshToken,
            request.InstallationId,
            cancellationToken);
        if (result.Status != RefreshStewardSessionStatus.Refreshed || result.Tokens is null)
        {
            return StewardApiResults.Problem(
                StatusCodes.Status401Unauthorized,
                "InvalidRefreshCredential",
                "The refresh credential is invalid or expired.");
        }

        return Results.Ok(new StewardApiResponse(
            "Refreshed",
            StewardSessionDto.From(result.Tokens)));
    }

    private static async Task<IResult> RevokeSessionAsync(
        RevokeSessionRequest request,
        StewardSessionService sessions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var revoked = await sessions.RevokeAsync(
            request.RefreshToken,
            request.InstallationId,
            cancellationToken);
        return Results.Ok(new StewardApiResponse(revoked ? "Revoked" : "NotRevoked"));
    }

    private static async Task<IResult> ListWorldsAsync(
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldMetadataService worlds,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var accessible = await worlds.ListAccessibleWorldsAsync(
            authentication.Caller!.Identity,
            cancellationToken);
        return Results.Ok(new StewardApiResponse(
            "WorldsListed",
            accessible.Select(SharedWorldDto.From).ToArray()));
    }

    private static async Task<IResult> CreateWorldAsync(
        CreateWorldRequest request,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldMetadataService worlds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var command = new CreateSharedWorldCommand(
            new WorldId(request.WorldId),
            request.AdapterId,
            request.DisplayName,
            new RevisionId(request.CurrentStateRevisionId),
            request.CurrentEnvironmentRevisionId is { } environmentId
                ? new RevisionId(environmentId)
                : null);
        var result = await worlds.CreateSharedWorldAsync(
            authentication.Caller!.Identity,
            command,
            cancellationToken);

        return result.Status switch
        {
            CreateSharedWorldStatus.Created => Results.Created(
                $"/api/v1/worlds/{request.WorldId:D}",
                new StewardApiResponse("WorldCreated", SharedWorldDto.From(result.World!))),
            CreateSharedWorldStatus.AlreadyExists => StewardApiResults.DomainConflict("WorldAlreadyExists"),
            _ => throw new InvalidOperationException("Unexpected create-World result.")
        };
    }

    private static async Task<IResult> GetWorldAsync(
        Guid worldId,
        HttpContext context,
        StewardSessionService sessions,
        SharedWorldMetadataService worlds,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var world = await worlds.GetAccessibleWorldAsync(
            authentication.Caller!.Identity,
            new WorldId(worldId),
            cancellationToken);
        return world is null
            ? StewardApiResults.NotFound("WorldNotFound")
            : Results.Ok(new StewardApiResponse("WorldFound", SharedWorldDto.From(world)));
    }

    private static async Task<IResult> GetCurrentRevisionAsync(
        Guid worldId,
        HttpContext context,
        StewardSessionService sessions,
        SharedRevisionMetadataService revisions,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var current = await revisions.GetCurrentRevisionMetadataAsync(
            authentication.Caller!.Identity,
            new WorldId(worldId),
            cancellationToken);
        return current is null
            ? StewardApiResults.NotFound("WorldNotFound")
            : Results.Ok(new StewardApiResponse(
                "CurrentRevisionFound",
                SharedCurrentRevisionDto.From(current)));
    }

    private static async Task<IResult> BeginTransferAsync(
        Guid worldId,
        BeginTransferRequest request,
        HttpContext context,
        StewardSessionService sessions,
        SharedPackageTransferService transfers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        if (!TryParsePackageKind(request.Kind, out var kind))
        {
            return StewardApiResults.Validation("InvalidPackageKind");
        }

        var command = new BeginPackageUploadCommand(
            new WorldId(worldId),
            new RevisionId(request.RevisionId),
            kind,
            request.ExpectedByteSize,
            request.ExpectedSha256,
            request.RequiredEnvironmentRevisionId is { } environmentId
                ? new RevisionId(environmentId)
                : null);
        var result = await transfers.BeginUploadAsync(
            authentication.Caller!.Identity,
            command,
            cancellationToken);

        return result.Status switch
        {
            BeginPackageUploadStatus.Started => Results.Created(
                $"/api/v1/transfers/{result.Transfer!.Id.Value:D}",
                new StewardApiResponse("TransferStarted", SharedTransferDto.From(result.Transfer))),
            BeginPackageUploadStatus.AlreadyPublished => Results.Ok(
                new StewardApiResponse("AlreadyPublished")),
            BeginPackageUploadStatus.NotFoundOrUnauthorized => StewardApiResults.NotFound("WorldNotFoundOrUnauthorized"),
            BeginPackageUploadStatus.InvalidRequest => StewardApiResults.Validation("InvalidTransferRequest"),
            BeginPackageUploadStatus.RequiredEnvironmentMissing => StewardApiResults.DomainConflict("RequiredEnvironmentMissing"),
            BeginPackageUploadStatus.Conflict => StewardApiResults.DomainConflict("RevisionConflict"),
            BeginPackageUploadStatus.StorageIntegrityFailure => StewardApiResults.DomainConflict("StorageIntegrityFailure"),
            _ => throw new InvalidOperationException("Unexpected begin-transfer result.")
        };
    }

    private static async Task<IResult> GetTransferProgressAsync(
        Guid transferId,
        HttpContext context,
        StewardSessionService sessions,
        SharedPackageTransferService transfers,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var progress = await transfers.GetProgressAsync(
            authentication.Caller!.Identity,
            new SharedPackageTransferId(transferId),
            cancellationToken);
        return progress is null
            ? StewardApiResults.NotFound("TransferNotFound")
            : Results.Ok(new StewardApiResponse(
                "TransferProgress",
                SharedTransferProgressDto.From(progress)));
    }

    private static async Task<IResult> AuthorizeTransferPartAsync(
        Guid transferId,
        int partNumber,
        HttpContext context,
        StewardSessionService sessions,
        SharedPackageTransferService transfers,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var result = await transfers.AuthorizePartAsync(
            authentication.Caller!.Identity,
            new SharedPackageTransferId(transferId),
            partNumber,
            cancellationToken);

        return result.Status switch
        {
            AuthorizePackagePartStatus.Authorized => Results.Ok(new StewardApiResponse(
                "PartAuthorized",
                DirectTransferAuthorizationDto.From(result.Authorization!))),
            AuthorizePackagePartStatus.TransferNotFound => StewardApiResults.NotFound("TransferNotFound"),
            AuthorizePackagePartStatus.TransferNotActive => StewardApiResults.DomainConflict("TransferNotActive"),
            AuthorizePackagePartStatus.Expired => StewardApiResults.DomainConflict("TransferExpired"),
            AuthorizePackagePartStatus.InvalidPart => StewardApiResults.Validation("InvalidPart"),
            _ => throw new InvalidOperationException("Unexpected part-authorization result.")
        };
    }

    private static async Task<IResult> FinalizeTransferAsync(
        Guid transferId,
        HttpContext context,
        StewardSessionService sessions,
        SharedPackageTransferService transfers,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        var result = await transfers.FinalizeAsync(
            authentication.Caller!.Identity,
            new SharedPackageTransferId(transferId),
            cancellationToken);

        return result.Status switch
        {
            FinalizePackageUploadStatus.Finalized => Results.Ok(new StewardApiResponse(
                "TransferFinalized",
                result.StoredObject is null ? null : StoredObjectDto.From(result.StoredObject))),
            FinalizePackageUploadStatus.AlreadyFinalized => Results.Ok(new StewardApiResponse(
                "AlreadyFinalized",
                result.StoredObject is null ? null : StoredObjectDto.From(result.StoredObject))),
            FinalizePackageUploadStatus.TransferNotFound => StewardApiResults.NotFound("TransferNotFound"),
            FinalizePackageUploadStatus.TransferNotActive => StewardApiResults.DomainConflict("TransferNotActive"),
            FinalizePackageUploadStatus.Expired => StewardApiResults.DomainConflict("TransferExpired"),
            FinalizePackageUploadStatus.IntegrityMismatch => StewardApiResults.DomainConflict("IntegrityMismatch"),
            FinalizePackageUploadStatus.PublicationConflict => StewardApiResults.DomainConflict("PublicationConflict"),
            FinalizePackageUploadStatus.PublicationBlocked => StewardApiResults.DomainConflict("PublicationBlocked"),
            _ => throw new InvalidOperationException("Unexpected finalize-transfer result.")
        };
    }

    private static async Task<IResult> AuthorizeDownloadAsync(
        Guid worldId,
        Guid revisionId,
        string kind,
        HttpContext context,
        StewardSessionService sessions,
        SharedPackageTransferService transfers,
        CancellationToken cancellationToken)
    {
        var authentication = await AuthenticateAsync(context, sessions, cancellationToken);
        if (authentication.Error is not null)
        {
            return authentication.Error;
        }

        if (!TryParsePackageKind(kind, out var parsedKind))
        {
            return StewardApiResults.NotFound("RevisionNotFound");
        }

        var result = await transfers.AuthorizeDownloadAsync(
            authentication.Caller!.Identity,
            new WorldId(worldId),
            new RevisionId(revisionId),
            parsedKind,
            cancellationToken);

        return result.Status switch
        {
            AuthorizePackageDownloadStatus.Authorized => Results.Ok(new StewardApiResponse(
                "DownloadAuthorized",
                new DownloadAuthorizationDto(
                    DirectTransferAuthorizationDto.From(result.Authorization!),
                    result.ExpectedByteSize!.Value,
                    result.ExpectedSha256!))),
            AuthorizePackageDownloadStatus.NotFoundOrUnauthorized => StewardApiResults.NotFound("RevisionNotFoundOrUnauthorized"),
            AuthorizePackageDownloadStatus.NoHostedPackage => StewardApiResults.DomainConflict("NoHostedPackage"),
            AuthorizePackageDownloadStatus.StorageIntegrityFailure => StewardApiResults.DomainConflict("StorageIntegrityFailure"),
            _ => throw new InvalidOperationException("Unexpected download-authorization result.")
        };
    }

    private static async Task<AuthenticationResolution> AuthenticateAsync(
        HttpContext context,
        StewardSessionService sessions,
        CancellationToken cancellationToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return new(null, StewardApiResults.AuthenticationRequired());
        }

        var token = authorization[prefix.Length..].Trim();
        if (token.Length == 0 || token.Contains(' '))
        {
            return new(null, StewardApiResults.AuthenticationRequired());
        }

        var caller = await sessions.ValidateAccessTokenAsync(token, cancellationToken);
        return caller is null
            ? new(null, StewardApiResults.AuthenticationRequired())
            : new(caller, null);
    }

    private static bool TryParsePackageKind(string? value, out SharedPackageKind kind)
    {
        if (Enum.TryParse(value, ignoreCase: true, out kind) &&
            kind is SharedPackageKind.State or SharedPackageKind.Environment)
        {
            return true;
        }

        kind = default;
        return false;
    }

    private sealed record AuthenticationResolution(
        StewardAuthenticatedCaller? Caller,
        IResult? Error);
}

public sealed record StewardApiResponse(
    string Code,
    object? Data = null,
    bool Retryable = false);

public sealed record SteamSessionRequest(
    string TicketHex,
    string InstallationId);

public sealed record RefreshSessionRequest(
    string RefreshToken,
    string InstallationId);

public sealed record RevokeSessionRequest(
    string RefreshToken,
    string InstallationId);

public sealed record CreateWorldRequest(
    Guid WorldId,
    string AdapterId,
    string DisplayName,
    Guid CurrentStateRevisionId,
    Guid? CurrentEnvironmentRevisionId);

public sealed record BeginTransferRequest(
    Guid RevisionId,
    string Kind,
    long ExpectedByteSize,
    string ExpectedSha256,
    Guid? RequiredEnvironmentRevisionId);

public sealed record StewardSessionDto(
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt)
{
    public static StewardSessionDto From(StewardSessionTokens tokens)
        => new(
            tokens.AccessToken,
            tokens.AccessExpiresAt,
            tokens.RefreshToken,
            tokens.RefreshExpiresAt);
}

public sealed record ExternalIdentityDto(
    string Provider,
    string ExternalId)
{
    public static ExternalIdentityDto From(ExternalIdentityRef identity)
        => new(identity.Provider, identity.ExternalId);
}

public sealed record SharedWorldDto(
    Guid WorldId,
    string AdapterId,
    string DisplayName,
    Guid CurrentStateRevisionId,
    Guid? CurrentEnvironmentRevisionId,
    ExternalIdentityDto AccessManager,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static SharedWorldDto From(SharedWorldMetadata world)
        => new(
            world.Id.Value,
            world.AdapterId,
            world.DisplayName,
            world.CurrentStateRevisionId.Value,
            world.CurrentEnvironmentRevisionId?.Value,
            ExternalIdentityDto.From(world.AccessManager),
            world.CreatedAt,
            world.UpdatedAt);
}

public sealed record SharedStateRevisionDto(
    Guid RevisionId,
    long ByteSize,
    string Sha256,
    Guid? RequiredEnvironmentRevisionId,
    DateTimeOffset PublishedAt)
{
    public static SharedStateRevisionDto From(SharedStateRevisionMetadata revision)
        => new(
            revision.RevisionId.Value,
            revision.ByteSize,
            revision.Sha256,
            revision.RequiredEnvironmentRevisionId?.Value,
            revision.PublishedAt);
}

public sealed record SharedEnvironmentRevisionDto(
    Guid RevisionId,
    string ArtifactReference,
    long? ByteSize,
    string? Sha256,
    DateTimeOffset PublishedAt)
{
    public static SharedEnvironmentRevisionDto From(SharedEnvironmentRevisionMetadata revision)
        => new(
            revision.RevisionId.Value,
            revision.ArtifactReference,
            revision.ByteSize,
            revision.Sha256,
            revision.PublishedAt);
}

public sealed record SharedCurrentRevisionDto(
    SharedWorldDto World,
    SharedStateRevisionDto? State,
    SharedEnvironmentRevisionDto? Environment)
{
    public static SharedCurrentRevisionDto From(SharedCurrentRevisionMetadata current)
        => new(
            SharedWorldDto.From(current.World),
            current.State is null ? null : SharedStateRevisionDto.From(current.State),
            current.Environment is null ? null : SharedEnvironmentRevisionDto.From(current.Environment));
}

public sealed record SharedTransferDto(
    Guid TransferId,
    Guid WorldId,
    Guid RevisionId,
    string Kind,
    long ExpectedByteSize,
    string ExpectedSha256,
    Guid? RequiredEnvironmentRevisionId,
    int PartSizeBytes,
    int PartCount,
    DateTimeOffset ExpiresAt,
    string State)
{
    public static SharedTransferDto From(SharedPackageTransferRecord transfer)
        => new(
            transfer.Id.Value,
            transfer.WorldId.Value,
            transfer.RevisionId.Value,
            transfer.Kind.ToString(),
            transfer.ExpectedByteSize,
            transfer.ExpectedSha256,
            transfer.RequiredEnvironmentRevisionId?.Value,
            transfer.PartSizeBytes,
            transfer.PartCount,
            transfer.ExpiresAt,
            transfer.State.ToString());
}

public sealed record UploadedPartDto(
    int PartNumber,
    long ByteSize);

public sealed record SharedTransferProgressDto(
    SharedTransferDto Transfer,
    IReadOnlyList<UploadedPartDto> CompletedParts,
    bool ProviderUploadCompleted)
{
    public static SharedTransferProgressDto From(SharedPackageTransferProgress progress)
        => new(
            SharedTransferDto.From(progress.Transfer),
            progress.CompletedParts
                .Select(part => new UploadedPartDto(part.PartNumber, part.ByteSize))
                .ToArray(),
            progress.ProviderUploadCompleted);
}

public sealed record DirectTransferAuthorizationDto(
    string Uri,
    string Method,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    DateTimeOffset ExpiresAt,
    long ExpectedByteSize)
{
    public static DirectTransferAuthorizationDto From(DirectObjectTransferAuthorization authorization)
        => new(
            authorization.Uri.AbsoluteUri,
            authorization.Method,
            authorization.RequiredHeaders,
            authorization.ExpiresAt,
            authorization.ExpectedByteSize);
}

public sealed record DownloadAuthorizationDto(
    DirectTransferAuthorizationDto Authorization,
    long ExpectedByteSize,
    string ExpectedSha256);

public sealed record StoredObjectDto(
    long ByteSize,
    string Sha256)
{
    public static StoredObjectDto From(ImmutableStoredObject stored)
        => new(stored.ByteSize, stored.Sha256);
}
