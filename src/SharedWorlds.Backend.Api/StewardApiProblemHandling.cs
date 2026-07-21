using SharedWorlds.Backend.Identity;

namespace SharedWorlds.Backend.Api;

public static class StewardApiProblemHandling
{
    public static IApplicationBuilder UseStewardApiProblemHandling(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (ExternalIdentityProviderException exception) when (!context.Response.HasStarted)
            {
                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("StewardApi");
                logger.LogWarning(
                    exception,
                    "External identity provider {Provider} failed. CorrelationId={CorrelationId}",
                    exception.Provider,
                    context.TraceIdentifier);
                context.Response.Clear();
                await StewardApiResults.Problem(
                        StatusCodes.Status503ServiceUnavailable,
                        "IdentityProviderUnavailable",
                        "External identity verification is temporarily unavailable.",
                        retryable: true,
                        context.TraceIdentifier)
                    .ExecuteAsync(context);
            }
            catch (ArgumentException exception) when (!context.Response.HasStarted)
            {
                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("StewardApi");
                logger.LogInformation(
                    exception,
                    "API request validation failed. CorrelationId={CorrelationId}",
                    context.TraceIdentifier);
                context.Response.Clear();
                await StewardApiResults.Problem(
                        StatusCodes.Status400BadRequest,
                        "InvalidRequest",
                        "The request is invalid.",
                        correlationId: context.TraceIdentifier)
                    .ExecuteAsync(context);
            }
            catch (Exception exception) when (!context.Response.HasStarted)
            {
                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("StewardApi");
                logger.LogError(
                    exception,
                    "Unhandled Steward API failure. CorrelationId={CorrelationId}",
                    context.TraceIdentifier);
                context.Response.Clear();
                await StewardApiResults.Problem(
                        StatusCodes.Status500InternalServerError,
                        "InternalFailure",
                        "The request could not be completed.",
                        retryable: true,
                        context.TraceIdentifier)
                    .ExecuteAsync(context);
            }
        });
    }
}

public static class StewardApiResults
{
    public static IResult AuthenticationRequired()
        => Problem(
            StatusCodes.Status401Unauthorized,
            "AuthenticationRequired",
            "A valid Steward access credential is required.");

    public static IResult NotFound(string code)
        => Results.Json(
            new StewardApiResponse(code),
            statusCode: StatusCodes.Status404NotFound);

    public static IResult Validation(string code)
        => Results.Json(
            new StewardApiResponse(code),
            statusCode: StatusCodes.Status422UnprocessableEntity);

    public static IResult DomainConflict(string code, bool retryable = false)
        => Results.Json(
            new StewardApiResponse(code, Retryable: retryable),
            statusCode: StatusCodes.Status409Conflict);

    public static IResult Problem(
        int statusCode,
        string code,
        string title,
        bool retryable = false,
        string? correlationId = null)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["retryable"] = retryable
        };
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            extensions["correlationId"] = correlationId;
        }

        return Results.Problem(
            type: $"urn:steward:problem:{ToProblemToken(code)}",
            title: title,
            statusCode: statusCode,
            extensions: extensions);
    }

    private static string ToProblemToken(string value)
        => string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? $"-{char.ToLowerInvariant(character)}"
                : char.ToLowerInvariant(character).ToString()));
}
