using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class PrivateSnapshotMaterializationApiContractTests
{
    [Fact]
    public void ServiceRequiresRevisionEvidenceBeforeObjectInspectionOrAuthorization()
    {
        var service = Read(
            "src/SharedWorlds.Backend/Transfers/PrivateSnapshotMaterializationDownloadService.cs");

        var byteAuthority = RequiredIndex(
            service,
            "var byteDecision = _snapshotAuthority.Resolve(");
        var evidenceLoad = RequiredIndex(
            service,
            "var evidence = await _revisionEvidence.LoadExactAsync(",
            byteAuthority);
        var materialization = RequiredIndex(
            service,
            "var materialization = _materializationAuthority.Resolve(",
            evidenceLoad);
        var objectInspection = RequiredIndex(
            service,
            "var stored = await _objectStore.InspectObjectAsync(",
            materialization);
        var authorization = RequiredIndex(
            service,
            "var authorization = await _objectStore.AuthorizeDownloadAsync(",
            objectInspection);

        Assert.True(byteAuthority < evidenceLoad);
        Assert.True(evidenceLoad < materialization);
        Assert.True(materialization < objectInspection);
        Assert.True(objectInspection < authorization);
        Assert.Contains(
            "selectedEvidence.StateRevision",
            service,
            StringComparison.Ordinal);
        Assert.Contains(
            "selectedEvidence.EnvironmentRevision",
            service,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RouteAuthenticatesBeforeMaterializationAuthority()
    {
        var api = Read(
            "src/SharedWorlds.Backend.Api/StewardPrivateSnapshotMaterializationApi.cs");

        Assert.Contains(
            "\"/api/v1/private-worlds/{worldId:guid}/materialization-download\"",
            api,
            StringComparison.Ordinal);
        var handler = RequiredIndex(
            api,
            "private static async Task<IResult> AuthorizeAsync(");
        var authentication = RequiredIndex(
            api,
            "var caller = await AuthenticateAsync(request, sessions, cancellationToken);",
            handler);
        var authority = RequiredIndex(
            api,
            "var result = await materialization.AuthorizeAsync(",
            authentication);

        Assert.True(handler < authentication);
        Assert.True(authentication < authority);
    }

    [Fact]
    public void AuthorizedPlanExposesExactRevisionRecordsButNoObjectKeyOrOwner()
    {
        var api = Read(
            "src/SharedWorlds.Backend.Api/StewardPrivateSnapshotMaterializationApi.cs");
        var data = Slice(
            api,
            "public sealed record PrivateSnapshotMaterializationData(",
            "public sealed record DirectTransferAuthorizationData(");

        Assert.Contains("StateRevision StateRevision", data, StringComparison.Ordinal);
        Assert.Contains(
            "EnvironmentRevision EnvironmentRevision",
            data,
            StringComparison.Ordinal);
        Assert.Contains("SourceInstallationId", data, StringComparison.Ordinal);
        Assert.DoesNotContain("ObjectKey", data, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerProvider", data, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerExternalId", data, StringComparison.Ordinal);
        Assert.DoesNotContain("ProviderUploadId", data, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMaterializationStatusHasExplicitHttpAndProtocolMapping()
    {
        var api = Read(
            "src/SharedWorlds.Backend.Api/StewardPrivateSnapshotMaterializationApi.cs");

        foreach (var code in new[]
                 {
                     "PrivateSnapshotMaterializationDownloadAuthorized",
                     "PrivateSnapshotNotFound",
                     "PrivateSnapshotMaterializationUnavailable",
                     "PrivateSnapshotAlreadyHere",
                     "PrivateSnapshotHeadConflict",
                     "PrivateSnapshotStorageIntegrityFailure"
                 })
        {
            Assert.Contains($"\"{code}\"", api, StringComparison.Ordinal);
        }

        Assert.Contains(
            "AuthorizePrivateSnapshotDownloadStatus.Authorized => Results.Ok",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "AuthorizePrivateSnapshotDownloadStatus.Unavailable => Results.Conflict",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "AuthorizePrivateSnapshotDownloadStatus.StorageIntegrityFailure => Results.Conflict",
            api,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionOwnsOneMaterializationServiceAndMapsItAfterSchemas()
    {
        var program = Read("src/SharedWorlds.Backend.Api/Program.cs");

        Assert.Equal(
            1,
            CountOccurrences(
                program,
                "new PrivateSnapshotMaterializationDownloadService("));
        Assert.Contains(
            "services.GetRequiredService<IOwnedWorldLocationStore>(),\n    services.GetRequiredService<IOwnedWorldSnapshotStore>(),\n    services.GetRequiredService<IOwnedWorldSnapshotRevisionEvidenceStore>(),\n    services.GetRequiredService<IPrivateImmutableObjectStore>(),",
            program,
            StringComparison.Ordinal);

        var evidenceInitialize = RequiredIndex(
            program,
            "GetRequiredService<PostgreSqlOwnedWorldSnapshotRevisionEvidenceStore>().InitializeAsync()");
        var route = RequiredIndex(
            program,
            "app.MapStewardPrivateSnapshotMaterializationApiV1();",
            evidenceInitialize);
        Assert.True(evidenceInitialize < route);
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
