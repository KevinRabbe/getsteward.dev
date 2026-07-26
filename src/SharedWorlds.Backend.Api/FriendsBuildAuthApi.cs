using SharedWorlds.Backend.Identity;

namespace SharedWorlds.Backend.Api;

public static class FriendsBuildAuthApi
{
    public static IEndpointRouteBuilder MapFriendsBuildAuthApiV1(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/api/v1/auth/friends/session", AuthenticateAsync);
        endpoints.MapGet("/api/v1/auth/friends/identities", ListIdentitiesAsync);
        return endpoints;
    }

    private static async Task<IResult> AuthenticateAsync(
        FriendsBuildSessionRequest request,
        FriendsBuildIdentityVerifier verifier,
        StewardSessionService sessionService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(sessionService);

        if (string.IsNullOrWhiteSpace(request.InstallationId) || request.InstallationId.Length > 256)
        {
            return Results.Json(
                new FriendsBuildAuthResponse("InvalidRequest", null, false),
                statusCode: StatusCodes.Status400BadRequest);
        }

        FriendsBuildIdentityVerificationResult verification;
        try
        {
            verification = verifier.Verify(request.Credential);
        }
        catch (ExternalIdentityProviderException exception)
        {
            return Results.Json(
                new FriendsBuildAuthResponse(
                    "IdentityProviderUnavailable",
                    null,
                    exception.Retryable),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (verification.Status != FriendsBuildIdentityVerificationStatus.Verified ||
            verification.Identity is null)
        {
            return Results.Json(
                new FriendsBuildAuthResponse("InvalidFriendsCredential", null, false),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var identity = verification.Identity;
        var displayName = identity.DisplayName
            ?? throw new InvalidOperationException(
                "Friends Build identity verification succeeded without a display name.");
        var tokens = await sessionService.CreateSessionAsync(
            identity,
            request.InstallationId,
            cancellationToken);
        var data = new FriendsBuildSessionData(
            tokens,
            new FriendsBuildIdentityData(
                identity.Subject.Provider,
                identity.Subject.ExternalId,
                displayName));
        return Results.Ok(new FriendsBuildAuthResponse("Authenticated", data, false));
    }

    private static async Task<IResult> ListIdentitiesAsync(
        HttpContext context,
        FriendsBuildIdentityVerifier verifier,
        StewardSessionService sessionService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(sessionService);

        var caller = await AuthenticateCallerAsync(context, sessionService, cancellationToken);
        if (caller is null)
        {
            return StewardApiResults.AuthenticationRequired();
        }

        if (!string.Equals(
                caller.Identity.Subject.Provider,
                FriendsBuildIdentityVerifier.Provider,
                StringComparison.Ordinal))
        {
            return Results.Json(
                new StewardApiResponse("FriendsBuildRosterUnavailable"),
                statusCode: StatusCodes.Status403Forbidden);
        }

        IReadOnlyList<FriendsBuildPublicIdentity> identities;
        try
        {
            identities = verifier.ListPublicIdentities();
        }
        catch (ExternalIdentityProviderException exception)
        {
            return Results.Json(
                new StewardApiResponse("IdentityProviderUnavailable", Retryable: exception.Retryable),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new StewardApiResponse(
            "FriendsBuildIdentitiesFound",
            identities.Select(static identity => new FriendsBuildPublicIdentityData(
                FriendsBuildIdentityVerifier.Provider,
                identity.ExternalId,
                identity.DisplayName)).ToArray()));
    }

    private static async Task<StewardAuthenticatedCaller?> AuthenticateCallerAsync(
        HttpContext context,
        StewardSessionService sessionService,
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

        return await sessionService.ValidateAccessTokenAsync(token, cancellationToken);
    }

    private sealed record FriendsBuildSessionRequest(
        string Credential,
        string InstallationId);

    private sealed record FriendsBuildAuthResponse(
        string Code,
        FriendsBuildSessionData? Data,
        bool Retryable);

    private sealed record FriendsBuildSessionData(
        StewardSessionTokens Tokens,
        FriendsBuildIdentityData Identity);

    private sealed record FriendsBuildIdentityData(
        string Provider,
        string ExternalId,
        string DisplayName);

    private sealed record FriendsBuildPublicIdentityData(
        string Provider,
        string ExternalId,
        string DisplayName);
}

public static class FriendsBuildAuthConfiguration
{
    public static FriendsBuildIdentityVerifier CreateVerifier(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = configuration.GetValue("FriendsBuild:Enabled", false);
        if (!enabled)
        {
            return FriendsBuildIdentityVerifier.CreateUnavailable();
        }

        var entries = configuration
            .GetSection("FriendsBuild:Identities")
            .GetChildren()
            .ToArray();
        if (entries.Length == 0)
        {
            throw new InvalidOperationException(
                "FriendsBuild:Enabled is true but no FriendsBuild:Identities are configured.");
        }

        if (entries.Length > FriendsBuildIdentityVerifier.MaximumConfiguredIdentities)
        {
            throw new InvalidOperationException(
                $"Friends Build supports at most {FriendsBuildIdentityVerifier.MaximumConfiguredIdentities} configured identities.");
        }

        try
        {
            return new FriendsBuildIdentityVerifier(entries.Select(entry =>
                new FriendsBuildIdentityDefinition(
                    RequireEntry(entry, "Id"),
                    RequireEntry(entry, "DisplayName"),
                    RequireEntry(entry, "CredentialSha256"))));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "Friends Build identity configuration is invalid.",
                exception);
        }
    }

    private static string RequireEntry(IConfigurationSection section, string key)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Friends Build identity '{section.Key}' is missing required field '{key}'.");
        }

        return value;
    }
}
