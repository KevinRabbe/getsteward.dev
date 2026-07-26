using SharedWorlds.Backend.Identity;

namespace SharedWorlds.Backend.Api;

public static class FriendsBuildAuthApi
{
    public static IEndpointRouteBuilder MapFriendsBuildAuthApiV1(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("/api/v1/auth/friends/session", AuthenticateAsync);
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

        var tokens = await sessionService.CreateSessionAsync(
            verification.Identity,
            request.InstallationId,
            cancellationToken);
        return Results.Ok(new FriendsBuildAuthResponse("Authenticated", tokens, false));
    }

    private sealed record FriendsBuildSessionRequest(
        string Credential,
        string InstallationId);

    private sealed record FriendsBuildAuthResponse(
        string Code,
        StewardSessionTokens? Data,
        bool Retryable);
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
