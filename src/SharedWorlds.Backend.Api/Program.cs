using System.Globalization;
using System.Text.Json.Serialization;
using Npgsql;
using SharedWorlds.Backend.Api;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.ObjectStorage.S3;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var connectionString = RequireConfiguration(builder.Configuration, "ConnectionStrings:Steward");
var steamAppId = ParseRequiredUInt32(builder.Configuration, "Steam:AppId");
var steamPublisherApiKey = RequireConfiguration(builder.Configuration, "Steam:PublisherApiKey");
var steamIdentity = RequireConfiguration(builder.Configuration, "Steam:Identity");
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

builder.Services.AddHttpClient("steam-identity");
builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());

builder.Services.AddSingleton<PostgreSqlSharedWorldStore>();
builder.Services.AddSingleton<ISharedWorldMetadataStore>(services =>
    services.GetRequiredService<PostgreSqlSharedWorldStore>());
builder.Services.AddSingleton<ISharedRevisionMetadataStore>(services =>
    services.GetRequiredService<PostgreSqlSharedWorldStore>());

builder.Services.AddSingleton<PostgreSqlSharedWorldAuthorityStore>();
builder.Services.AddSingleton<ISharedWorldAuthorityStore>(services =>
    services.GetRequiredService<PostgreSqlSharedWorldAuthorityStore>());
builder.Services.AddSingleton<ISharedWorldResponsibilityInspector>(services =>
    services.GetRequiredService<PostgreSqlSharedWorldAuthorityStore>());

builder.Services.AddSingleton<PostgreSqlStewardSessionStore>();
builder.Services.AddSingleton<IStewardSessionStore>(services =>
    services.GetRequiredService<PostgreSqlStewardSessionStore>());

builder.Services.AddSingleton<PostgreSqlSharedPackageTransferStore>();
builder.Services.AddSingleton<ISharedPackageTransferStore>(services =>
    services.GetRequiredService<PostgreSqlSharedPackageTransferStore>());

builder.Services.AddSingleton<IPrivateImmutableObjectStore>(_ =>
    S3CompatibleObjectStoreFactory.Create(new S3CompatibleObjectStoreOptions(
        objectStorageServiceUrl,
        objectStorageRegion,
        objectStorageBucket,
        objectStorageAccessKey,
        objectStorageSecretKey,
        objectStorageForcePathStyle)));

builder.Services.AddSingleton(services => new SteamWebApiTicketVerifier(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("steam-identity"),
    new SteamWebApiTicketVerifierOptions(
        steamAppId,
        steamPublisherApiKey,
        steamIdentity)));
builder.Services.AddSingleton(services => new StewardSessionService(
    services.GetRequiredService<IStewardSessionStore>(),
    () => DateTimeOffset.UtcNow));
builder.Services.AddSingleton(services => new SharedWorldMetadataService(
    services.GetRequiredService<ISharedWorldMetadataStore>(),
    () => DateTimeOffset.UtcNow));
builder.Services.AddSingleton<SharedRevisionMetadataService>();
builder.Services.AddSingleton(services => new SharedWorldAuthorityService(
    services.GetRequiredService<ISharedWorldAuthorityStore>(),
    () => DateTimeOffset.UtcNow));
builder.Services.AddSingleton(services => new SharedPackageTransferService(
    services.GetRequiredService<ISharedWorldMetadataStore>(),
    services.GetRequiredService<SharedRevisionMetadataService>(),
    services.GetRequiredService<ISharedPackageTransferStore>(),
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

var app = builder.Build();

await PostgreSqlBackendSchema.InitializeAsync(app.Services.GetRequiredService<NpgsqlDataSource>());

app.UseStewardApiProblemHandling();
app.MapStewardApiV1();
app.MapStewardAuthorityApiV1();

await app.RunAsync();

static string RequireConfiguration(IConfiguration configuration, string key)
{
    var value = configuration[key];
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Required configuration '{key}' is missing.");
    }

    return value;
}

static uint ParseRequiredUInt32(IConfiguration configuration, string key)
{
    var value = RequireConfiguration(configuration, key);
    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
    {
        throw new InvalidOperationException($"Required configuration '{key}' must be a positive UInt32.");
    }

    return parsed;
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
