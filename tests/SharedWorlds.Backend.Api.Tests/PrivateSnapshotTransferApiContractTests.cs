using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class PrivateSnapshotTransferApiContractTests
{
    [Fact]
    public void RoutesAreExplicitAndEveryHandlerAuthenticatesBeforeTransferAuthority()
    {
        var api = Read("src/SharedWorlds.Backend.Api/StewardPrivateSnapshotTransferApi.cs");

        Assert.Contains(
            "\"/api/v1/private-worlds/{worldId:guid}/snapshot-upload\"",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"/api/v1/private-snapshot-transfers/{transferId:guid}\"",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"/api/v1/private-snapshot-transfers/{transferId:guid}/parts/{partNumber:int}/authorization\"",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"/api/v1/private-snapshot-transfers/{transferId:guid}/finalize\"",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"/api/v1/private-worlds/{worldId:guid}/snapshot-download\"",
            api,
            StringComparison.Ordinal);

        AssertAuthenticationBefore(
            api,
            "private static async Task<IResult> BeginUploadAsync(",
            "transfers.BeginUploadAsync(");
        AssertAuthenticationBefore(
            api,
            "private static async Task<IResult> GetProgressAsync(",
            "transfers.GetProgressAsync(");
        AssertAuthenticationBefore(
            api,
            "private static async Task<IResult> AuthorizePartAsync(",
            "transfers.AuthorizePartAsync(");
        AssertAuthenticationBefore(
            api,
            "private static async Task<IResult> FinalizeAsync(",
            "transfers.FinalizeAsync(");
        AssertAuthenticationBefore(
            api,
            "private static async Task<IResult> AuthorizeDownloadAsync(",
            "transfers.AuthorizeDownloadAsync(");
    }

    [Fact]
    public void OwnershipAndStorageIdentityCannotBeSuppliedOrReturnedOverHttp()
    {
        var api = Read("src/SharedWorlds.Backend.Api/StewardPrivateSnapshotTransferApi.cs");
        var request = Slice(
            api,
            "public sealed record BeginPrivateSnapshotUploadRequest(",
            "public sealed record PrivateSnapshotTransferData(");
        var responseDtos = api[RequiredIndex(
            api,
            "public sealed record PrivateSnapshotTransferData(")..];

        Assert.DoesNotContain("OwnerProvider", request, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerExternalId", request, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallationId", request, StringComparison.Ordinal);
        Assert.DoesNotContain("ObjectKey", request, StringComparison.Ordinal);
        Assert.DoesNotContain("ProviderUploadId", request, StringComparison.Ordinal);

        Assert.DoesNotContain("OwnerProvider", responseDtos, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerExternalId", responseDtos, StringComparison.Ordinal);
        Assert.DoesNotContain("ObjectKey", responseDtos, StringComparison.Ordinal);
        Assert.DoesNotContain("ProviderUploadId", responseDtos, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PrivateSnapshotTransferRecord Transfer",
            responseDtos,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PrivateSnapshotDownloadPlan Plan",
            responseDtos,
            StringComparison.Ordinal);

        Assert.Contains(
            "new WorldId(worldId)",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "new PrivateSnapshotTransferId(transferId)",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "ValidateAccessTokenAsync(accessToken, cancellationToken)",
            api,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsesMapAuthorityStatusesAndUseExplicitIntegerStateCodes()
    {
        var api = Read("src/SharedWorlds.Backend.Api/StewardPrivateSnapshotTransferApi.cs");

        foreach (var code in new[]
                 {
                     "PrivateSnapshotUploadStarted",
                     "PrivateSnapshotAlreadyPublished",
                     "PrivateSnapshotPartAuthorized",
                     "PrivateSnapshotTransferProgress",
                     "PrivateSnapshotFinalized",
                     "PrivateSnapshotPublicationBlocked",
                     "PrivateSnapshotDownloadAuthorized",
                     "PrivateSnapshotAlreadyHere",
                     "PrivateSnapshotHeadConflict",
                     "PrivateSnapshotStorageIntegrityFailure"
                 })
        {
            Assert.Contains($"\"{code}\"", api, StringComparison.Ordinal);
        }

        Assert.Contains("(int)transfer.State", api, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PrivateSnapshotTransferState State,",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "new PrivateSnapshotPartAuthorizationData(",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "new PrivateSnapshotDownloadData(",
            api,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionCompositionUsesExactStoresStartupOrderAndSharedObjectStore()
    {
        var program = Read("src/SharedWorlds.Backend.Api/Program.cs");

        Assert.Contains(
            "builder.Services.AddSingleton<PostgreSqlOwnedWorldSnapshotStore>();",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "builder.Services.AddSingleton<PostgreSqlBringHereOwnedWorldSnapshotStore>();",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "builder.Services.AddSingleton<IOwnedWorldSnapshotStore>(services =>\n    services.GetRequiredService<PostgreSqlBringHereOwnedWorldSnapshotStore>());",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "builder.Services.AddSingleton<PostgreSqlPrivateSnapshotTransferStore>();",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "builder.Services.AddSingleton<IPrivateSnapshotTransferStore>(services =>\n    services.GetRequiredService<PostgreSqlPrivateSnapshotTransferStore>());",
            program,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(program, "new PrivateSnapshotTransferService("));
        Assert.Contains(
            "services.GetRequiredService<IPrivateImmutableObjectStore>()",
            program,
            StringComparison.Ordinal);

        var locationInitialize = RequiredIndex(
            program,
            "GetRequiredService<PostgreSqlOwnedWorldLocationStore>().InitializeAsync()");
        var snapshotInitialize = RequiredIndex(
            program,
            "GetRequiredService<PostgreSqlOwnedWorldSnapshotStore>().InitializeAsync()",
            locationInitialize);
        var transferInitialize = RequiredIndex(
            program,
            "GetRequiredService<PostgreSqlPrivateSnapshotTransferStore>().InitializeAsync()",
            snapshotInitialize);
        var map = RequiredIndex(
            program,
            "app.MapStewardPrivateSnapshotTransferApiV1();",
            transferInitialize);

        Assert.True(locationInitialize < snapshotInitialize);
        Assert.True(snapshotInitialize < transferInitialize);
        Assert.True(transferInitialize < map);
    }

    [Fact]
    public void BringHereSnapshotReadUsesBoundedOverflowProbeWithoutChangingPersistenceWrites()
    {
        var decorator = Read(
            "src/SharedWorlds.Backend.PostgreSql/PostgreSqlBringHereOwnedWorldSnapshotStore.cs");
        var program = Read("src/SharedWorlds.Backend.Api/Program.cs");
        var service = Read(
            "src/SharedWorlds.Backend/Transfers/PrivateSnapshotTransferService.cs");

        Assert.Contains(
            "maximumSnapshots > normalLimit + 1",
            decorator,
            StringComparison.Ordinal);
        Assert.Contains(
            "LIMIT @overflow_probe_limit",
            decorator,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (count > normalLimit)",
            decorator,
            StringComparison.Ordinal);
        Assert.Contains(
            "=> _inner.PublishAsync(snapshot, cancellationToken);",
            decorator,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetRequiredService<PostgreSqlBringHereOwnedWorldSnapshotStore>()",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "BringHereSnapshotAuthorityService.MaximumSnapshotsPerWorld + 1",
            service,
            StringComparison.Ordinal);
    }

    private static void AssertAuthenticationBefore(
        string source,
        string handlerMarker,
        string operationMarker)
    {
        var handler = RequiredIndex(source, handlerMarker);
        var authentication = RequiredIndex(
            source,
            "var caller = await AuthenticateAsync(request, sessions, cancellationToken);",
            handler);
        var operation = RequiredIndex(source, operationMarker, authentication);
        Assert.True(authentication < operation);
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = RequiredIndex(source, start);
        var endIndex = RequiredIndex(source, end, startIndex);
        return source[startIndex..endIndex];
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string Read(string relativePath)
        => File.ReadAllText(FindRepositoryFile(relativePath));

    private static string FindRepositoryFile(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
