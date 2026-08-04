using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class PrivateSnapshotRevisionEvidenceApiContractTests
{
    [Fact]
    public void RouteAuthenticatesBeforeEvidenceAuthority()
    {
        var api = Read(
            "src/SharedWorlds.Backend.Api/StewardPrivateSnapshotRevisionEvidenceApi.cs");

        Assert.Contains(
            "\"/api/v1/private-worlds/{worldId:guid}/snapshot-revision-evidence\"",
            api,
            StringComparison.Ordinal);
        var handler = RequiredIndex(
            api,
            "private static async Task<IResult> PublishAsync(");
        var authentication = RequiredIndex(
            api,
            "var caller = await AuthenticateAsync(request, sessions, cancellationToken);",
            handler);
        var authority = RequiredIndex(
            api,
            "var result = await evidence.PublishAsync(",
            authentication);

        Assert.True(handler < authentication);
        Assert.True(authentication < authority);
    }

    [Fact]
    public void RequestCannotSupplyOwnerInstallationOrEvidenceTime()
    {
        var api = Read(
            "src/SharedWorlds.Backend.Api/StewardPrivateSnapshotRevisionEvidenceApi.cs");
        var request = Slice(
            api,
            "public sealed record PublishPrivateSnapshotRevisionEvidenceRequest(",
            "public sealed record PrivateSnapshotRevisionEvidenceData(");
        var response = Slice(
            api,
            "public sealed record PrivateSnapshotRevisionEvidenceData(",
            "public sealed record PrivateSnapshotRevisionEvidenceResponse(");

        Assert.Contains("StateRevision? StateRevision", request, StringComparison.Ordinal);
        Assert.Contains(
            "EnvironmentRevision? EnvironmentRevision",
            request,
            StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerProvider", request, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerExternalId", request, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallationId", request, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordedAt", request, StringComparison.Ordinal);

        Assert.Contains("DateTimeOffset RecordedAt", response, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerProvider", response, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerExternalId", response, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallationId", response, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryServiceStatusHasExplicitHttpAndProtocolMapping()
    {
        var api = Read(
            "src/SharedWorlds.Backend.Api/StewardPrivateSnapshotRevisionEvidenceApi.cs");

        foreach (var code in new[]
                 {
                     "PrivateSnapshotRevisionEvidencePublished",
                     "PrivateSnapshotRevisionEvidenceAlreadyPublished",
                     "PrivateSnapshotNotFound",
                     "PrivateSnapshotRevisionEvidenceInvalid",
                     "PrivateSnapshotRevisionEvidenceConflict"
                 })
        {
            Assert.Contains($"\"{code}\"", api, StringComparison.Ordinal);
        }

        Assert.Contains(
            "PublishPrivateSnapshotRevisionEvidenceStatus.Published => Results.Ok",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublishPrivateSnapshotRevisionEvidenceStatus.AlreadyPublished => Results.Ok",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublishPrivateSnapshotRevisionEvidenceStatus.NotFoundOrUnauthorized => Results.NotFound",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublishPrivateSnapshotRevisionEvidenceStatus.InvalidRequest => Results.BadRequest",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "PublishPrivateSnapshotRevisionEvidenceStatus.Conflict => Results.Conflict",
            api,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionOwnsOneStoreServiceAndExactStartupOrder()
    {
        var program = Read("src/SharedWorlds.Backend.Api/Program.cs");

        Assert.Equal(
            1,
            CountOccurrences(
                program,
                "builder.Services.AddSingleton<PostgreSqlOwnedWorldSnapshotRevisionEvidenceStore>();"));
        Assert.Equal(
            1,
            CountOccurrences(
                program,
                "builder.Services.AddSingleton<IOwnedWorldSnapshotRevisionEvidenceStore>(services =>"));
        Assert.Equal(
            1,
            CountOccurrences(
                program,
                "new PrivateSnapshotRevisionEvidenceService("));
        Assert.Contains(
            "services.GetRequiredService<IOwnedWorldSnapshotStore>(),\n    services.GetRequiredService<IOwnedWorldSnapshotRevisionEvidenceStore>(),",
            program,
            StringComparison.Ordinal);

        var snapshotInitialize = RequiredIndex(
            program,
            "GetRequiredService<PostgreSqlOwnedWorldSnapshotStore>().InitializeAsync()");
        var evidenceInitialize = RequiredIndex(
            program,
            "GetRequiredService<PostgreSqlOwnedWorldSnapshotRevisionEvidenceStore>().InitializeAsync()",
            snapshotInitialize);
        var transferInitialize = RequiredIndex(
            program,
            "GetRequiredService<PostgreSqlPrivateSnapshotTransferStore>().InitializeAsync()",
            evidenceInitialize);
        var route = RequiredIndex(
            program,
            "app.MapStewardPrivateSnapshotRevisionEvidenceApiV1();",
            transferInitialize);

        Assert.True(snapshotInitialize < evidenceInitialize);
        Assert.True(evidenceInitialize < transferInitialize);
        Assert.True(transferInitialize < route);
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
