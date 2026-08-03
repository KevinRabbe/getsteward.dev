using System.Globalization;
using System.Text.Json.Serialization;
using Npgsql;
using SharedWorlds.Backend.Api;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.ObjectStorage.S3;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Worlds;

var builder = WebApplication.CreateBuilder(args);
ConfigureListenPort(builder);
var useForwardedClientAddress = StewardForwardedClientAddress.Configure(
    builder.Services,
    builder.Configuration);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var connectionString = RequireConfiguration(builder.Configuration, "ConnectionStrings:Steward");
var steamVerifierOptions = ReadOptionalSteamVerifierOptions(builder.Configuration);
var friendsBuildVerifier = FriendsBuildAuthConfiguration.CreateVerifier(builder.Configuration);
var objectStorageServiceUrl = new Uri(
    RequireConfiguration(builder.Configuration, "ObjectStorage:ServiceUrl"),
    UriKind.Absolute);
var objectStorageRegion = RequireConfiguration(builder.Configuration, "ObjectStorage:AuthenticationRegion");
var objectStorageBucket = RequireConfiguration(builder.Configuration, "ObjectStorage:BucketName");
var objectStorageAccessKey = RequireConfiguration(builder.Configuration, "ObjectStorage:AccessKeyId");
var objectStorageSecretKey = RequireConfiguration(builder.Configuration, "ObjectStorage:SecretAccessKey");
var objectStorageForcePathStyle = builder.Configuration.GetValue("ObjectStorage:ForcePathStyle", false);

var cleanupIntervalMinutes = ParseBoundedInt32(
    builder.Configuration,
    "Cleanup:IntervalMinutes",
    defaultValue: 15,
    minimum: 1,
    maximum: 24 * 60);
var cleanupVerifiedCandidateRetentionDays = ParseBoundedInt32(
    builder.Configuration,
    "Cleanup:VerifiedCandidateRetentionDays",
    defaultValue: 7,
    minimum: 1,
    maximum: 365);
var cleanupBatchSize = ParseBoundedInt32(
    builder.Configuration,
    "Cleanup:BatchSize",
    defaultValue: 100,
    minimum: 1,
    maximum: 1000);

builder.Services
    .AddHttpClient("steam-identity", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false
    });
builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());

builder.Services.AddSingleton<PostgreSqlSharedWorldStore>();
builder.Services.AddSingleton<ISharedWorldMetadataStore>(services =>
    services.GetRequiredService<PostgreSqlSharedWorldStore>());
builder.Services.AddSingleton<ISharedRevisionMetadataStore>(services =>
    services.GetRequiredService<PostgreSqlSharedWorldStore>());
builder.Services.AddSingleton<PostgreSqlSharedWorldAccessStore>();
builder.Services.AddSingleton<ISharedWorldAccessStore>(services =>
    services.GetRequiredService<PostgreSqlSharedWorldAccessStore>());

builder.Services.AddSingleton<PostgreSqlSharedWorldAuthorityStore>();
builder.Services.AddSingleton(services => new PostgreSqlIdempotentSharedWorldAuthorityStore(
    services.GetRequiredService<NpgsqlDataSource>(),
    services.GetRequiredService<PostgreSqlSharedWorldAuthorityStore>()));
builder.Services.AddSingleton(services => new PostgreSqlIdempotentReservationAuthorityStore(
    services.GetRequiredService<NpgsqlDataSource>(),
    services.GetRequiredService<PostgreSqlIdempotentSharedWorldAuthorityStore>()));
builder.Services.AddSingleton<ISharedWorldAuthorityStore>(services =>
    services.GetRequiredService<PostgreSqlIdempotentReservationAuthorityStore>());
builder.Services.AddSingleton<ISharedWorldResponsibilityInspector>(services =>
    services.GetRequiredService<PostgreSqlSharedWorldAuthorityStore>());
builder.Services.AddSingleton<PostgreSqlSharedWorldReservationAbandonStore>();
builder.Services.AddSingleton<ISharedWorldReservationAbandonStore>(services =>
    services.GetRequiredService<PostgreSqlSharedWorldReservationAbandonStore>());

builder.Services.AddSingleton<PostgreSqlSharedWorldHostPresenceStore>();
builder.Services.AddSingleton<ISharedWorldHostPresenceStore>(services =>
    services.GetRequiredService<PostgreSqlSharedWorldHostPresenceStore>());
builder.Services.AddSingleton<PostgreSqlSharedWorldPlayerPresenceStore>();
builder.Services.AddSingleton<ISharedWorldPlayerPresenceStore>(services =>
    services.GetRequiredService<PostgreSqlSharedWorldPlayerPresenceStore>());

builder.Services.AddSingleton<PostgreSqlStewardSessionStore>();
builder.Services.AddSingleton<IStewardSessionStore>(services =>
    services.GetRequiredService<PostgreSqlStewardSessionStore>());
builder.Services.AddSingleton<PostgreSqlOwnedWorldLocationStore>();
builder.Services.AddSingleton<IOwnedWorldLocationStore>(services =>
    services.GetRequiredService<PostgreSqlOwnedWorldLocationStore>());
builder.Services.AddSingleton<PostgreSqlOwnedWorldSnapshotStore>();
builder.Services.AddSingleton<IOwnedWorldSnapshotStore>(services =>
    services.GetRequiredService<PostgreSqlOwnedWorldSnapshotStore>());
builder.Services.AddSingleton<PostgreSqlPrivateSnapshotTransferStore>();
builder.Services.AddSingleton<IPrivateSnapshotTransferStore>(services =>
    services.GetRequiredService<PostgreSqlPrivateSnapshotTransferStore>());

builder.Services.AddSingleton<PostgreSqlSharedPackageTransferStore>();
builder.Services.AddSingleton<ISharedPackageTransferStore>(services =>
    services.GetRequiredService<PostgreSqlSharedPackageTransferStore>());

builder.Services.AddSingleton<PostgreSqlSharedRevisionRetentionStore>();
builder.Services.AddSingleton<ISharedRevisionRetentionStore>(services =>
    services.GetRequiredService<PostgreSqlSharedRevisionRetentionStore>());

builder.Services.AddSingleton<IPrivateImmutableObjectStore>(_ =>
    S3CompatibleObjectStoreFactory.Create(new S3CompatibleObjectStoreOptions(
        objectStorageServiceUrl,
        objectStorageRegion,
        objectStorageBucket,
        objectStorageAccessKey,
        objectStorageSecretKey,
        objectStorageForcePathStyle)));

builder.Services.AddSingleton(services =>
{
    var httpClient = services.GetRequiredService<IHttpClientFactory>().CreateClient("steam-identity");
    return steamVerifierOptions is null
        ? SteamWebApiTicketVerifier.CreateUnavailable(httpClient)
        : new SteamWebApiTicketVerifier(httpClient, steamVerifierOptions);
});
builder.Services.AddSingleton(friendsBuildVerifier);
builder.Services.AddSingleton(services => new StewardSessionService(
    services.GetRequiredService<IStewardSessionStore>(),
    () => DateTimeOffset.UtcNow));
builder.Services.AddSingleton(services => new OwnedWorldLocationApplicationService(
    services.GetRequiredService<IOwnedWorldLocationStore>(),
    () => DateTimeOffset.UtcNow));
builder.Services.AddSingleton(services => new SharedWorldMetadataService(
    services.GetRequiredService<ISharedWorldMetadataStore>(),
    () => DateTimeOffset.UtcNow));
builder.Services.AddSingleton<SharedRevisionMetadataService>();
builder.Services.AddSingleton(services => new SharedWorldAuthorityService(
    services.GetRequiredService<ISharedWorldAuthorityStore>(),
    () => DateTimeOffset.UtcNow));
builder.Services.AddSingleton<SharedWorldReservationAbandonService>();
builder.Services.AddSingleton(services => new SharedWorldHostPresenceService(
    services.GetRequiredService<SharedWorldAuthorityService>(),
    services.GetRequiredService<ISharedWorldHostPresenceStore>(),
    () => DateTimeOffset.UtcNow));
builder.Services.AddSingleton(services => new SharedWorldAccessService(
    services.GetRequiredService<ISharedWorldMetadataStore>(),
    services.GetRequiredService<ISharedWorldAccessStore>(),
    services.GetRequiredService<ISharedWorldResponsibilityInspector>(),
    () => DateTimeOffset.UtcNow));
builder.Services.AddSingleton(services => new SharedWorldPlayerPresenceService(
    services.GetRequiredService<SharedWorldAccessService>(),
    services.GetRequiredService<ISharedWorldPlayerPresenceStore>(),
    () => DateTimeOffset.UtcNow));
builder.Services.AddSingleton(services => new SharedPackageTransferService(
    services.GetRequiredService<ISharedWorldMetadataStore>(),
    services.GetRequiredService<SharedRevisionMetadataService>(),
    services.GetRequiredService<ISharedPackageTransferStore>(),
    services.GetRequiredService<IPrivateImmutableObjectStore>(),
    () => DateTimeOffset.UtcNow));
builder.Services.AddSingleton(services => new PrivateSnapshotTransferService(
    services.GetRequiredService<IOwnedWorldLocationStore>(),
    services.GetRequiredService<IOwnedWorldSnapshotStore>(),
    services.GetRequiredService<IPrivateSnapshotTransferStore>(),
    services.GetRequiredService<IPrivateImmutableObjectStore>(),
    () => DateTimeOffset.UtcNow));

builder.Services.AddSingleton(new SharedPackageTransferCleanupOptions(
    TimeSpan.FromDays(cleanupVerifiedCandidateRetentionDays),
    cleanupBatchSize));
builder.Services.AddSingleton(new SharedPackageTransferCleanupWorkerOptions(
    TimeSpan.FromMinutes(cleanupIntervalMinutes)));
builder.Services.AddSingleton(services => new SharedPackageTransferCleanupService(
    services.GetRequiredService<ISharedPackageTransferStore>(),
    services.GetRequiredService<ISharedRevisionMetadataStore>(),
    services.GetRequiredService<IPrivateImmutableObjectStore>(),
    () => DateTimeOffset.UtcNow,
    services.GetRequiredService<SharedPackageTransferCleanupOptions>()));
builder.Services.AddHostedService<SharedPackageTransferCleanupWorker>();

builder.Services.AddSingleton(new SharedRevisionRetentionOptions(
    retainedCanonicalHeadCount: 3,
    uncommittedCandidateGrace: TimeSpan.FromDays(cleanupVerifiedCandidateRetentionDays),
    cleanupBatchSize: cleanupBatchSize));
builder.Services.AddSingleton(services => new SharedRevisionRetentionCleanupService(
    services.GetRequiredService<ISharedRevisionRetentionStore>(),
    services.GetRequiredService<IPrivateImmutableObjectStore>(),
    () => DateTimeOffset.UtcNow,
    services.GetRequiredService<SharedRevisionRetentionOptions>()));
builder.Services.AddHostedService<SharedRevisionRetentionCleanupWorker>();

var app = builder.Build();
if (useForwardedClientAddress)
{
    app.UseForwardedHeaders();
}

var dataSource = app.Services.GetRequiredService<NpgsqlDataSource>();
await PostgreSqlBackendSchema.InitializeAsync(dataSource);
await PostgreSqlSharedWorldHostPresenceSchema.InitializeAsync(dataSource);
await PostgreSqlSharedWorldPlayerPresenceSchema.InitializeAsync(dataSource);
await app.Services.GetRequiredService<PostgreSqlOwnedWorldLocationStore>().InitializeAsync();
await app.Services.GetRequiredService<PostgreSqlOwnedWorldSnapshotStore>().InitializeAsync();
await app.Services.GetRequiredService<PostgreSqlPrivateSnapshotTransferStore>().InitializeAsync();

app.UseStewardApiProblemHandling();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", CheckReadinessAsync);
app.MapStewardApiV1();
app.MapFriendsBuildAuthApiV1();
app.MapStewardAccessApiV1();
app.MapStewardRevisionMetadataApiV1();
app.MapStewardAuthorityApiV1();
app.MapStewardReservationAbandonApiV1();
app.MapStewardHostPresenceApiV1();
app.MapStewardWorldPlayerPresenceApiV1();
app.MapStewardOwnedWorldLocationApiV1();
app.MapStewardPrivateSnapshotTransferApiV1();

await app.RunAsync();

static void ConfigureListenPort(WebApplicationBuilder builder)
{
    var value = builder.Configuration["PORT"];
    if (string.IsNullOrWhiteSpace(value))
    {
        return;
    }

    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
        port is < 1 or > 65535)
    {
        throw new InvalidOperationException("Configuration 'PORT' must be an integer between 1 and 65535.");
    }

    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

static async Task<IResult> CheckReadinessAsync(
    NpgsqlDataSource dataSource,
    IPrivateImmutableObjectStore objectStore,
    CancellationToken cancellationToken)
{
    const string readinessObjectKey = "health/readiness/never-written-probe";

    try
    {
        await using var command = dataSource.CreateCommand("SELECT 1;");
        var databaseResult = await command.ExecuteScalarAsync(cancellationToken);
        if (databaseResult is null)
        {
            return Results.Json(
                new { status = "not-ready" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        _ = await objectStore.InspectObjectAsync(readinessObjectKey, cancellationToken);
        return Results.Ok(new { status = "ready" });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch
    {
        return Results.Json(
            new { status = "not-ready" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

static SteamWebApiTicketVerifierOptions? ReadOptionalSteamVerifierOptions(IConfiguration configuration)
{
    var appId = configuration["Steam:AppId"];
    var publisherApiKey = configuration["Steam:PublisherApiKey"];
    var identity = configuration["Steam:Identity"];

    if (string.IsNullOrWhiteSpace(appId) &&
        string.IsNullOrWhiteSpace(publisherApiKey) &&
        string.IsNullOrWhiteSpace(identity))
    {
        return null;
    }

    if (string.IsNullOrWhiteSpace(appId) ||
        string.IsNullOrWhiteSpace(publisherApiKey) ||
        string.IsNullOrWhiteSpace(identity))
    {
        throw new InvalidOperationException(
            "Steam authentication is partially configured. Provide Steam:AppId, Steam:PublisherApiKey, and Steam:Identity together, or omit all three to disable Steam authentication for infrastructure-only deployment.");
    }

    if (!uint.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedAppId) ||
        parsedAppId == 0)
    {
        throw new InvalidOperationException("Configuration 'Steam:AppId' must be a positive UInt32.");
    }

    return new SteamWebApiTicketVerifierOptions(parsedAppId, publisherApiKey, identity);
}

static string RequireConfiguration(IConfiguration configuration, string key)
{
    var value = configuration[key];
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Required configuration '{key}' is missing.");
    }

    return value;
}

static int ParseBoundedInt32(
    IConfiguration configuration,
    string key,
    int defaultValue,
    int minimum,
    int maximum)
{
    var value = configuration[key];
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
        parsed < minimum ||
        parsed > maximum)
    {
        throw new InvalidOperationException(
            $"Configuration '{key}' must be an integer between {minimum} and {maximum}.");
    }

    return parsed;
}

public partial class Program
{
}
