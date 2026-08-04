using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PrivateSnapshotMaterializationDesktopCompositionTests
{
    [Fact]
    public void RuntimeReusesOneSessionApiAndVerifiedCacheForPreparation()
    {
        var runtime = Read("src/SharedWorlds.Desktop/StewardDesktopRemoteRuntime.cs");

        Assert.Equal(1, CountOccurrences(runtime, "new StewardAccessSession("));
        Assert.Equal(1, CountOccurrences(runtime, "new VerifiedPackageCache("));
        Assert.Equal(
            1,
            CountOccurrences(
                runtime,
                "new StewardPrivateSnapshotMaterializationClient("));
        Assert.Contains(
            "new StewardPrivateSnapshotMaterializationClient(\n                    apiClient,\n                    verifiedCache,\n                    GetAccessTokenAsync);",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "=> _privateSnapshotMaterialization.EnsureDownloadedAsync(\n            worldId,\n            cancellationToken);",
            runtime,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreparationHasNoLocalMutationAuthority()
    {
        var runtime = Read("src/SharedWorlds.Desktop/StewardDesktopRemoteRuntime.cs");
        var client = Read(
            "src/SharedWorlds.Infrastructure/Remote/StewardPrivateSnapshotMaterializationClient.cs");

        Assert.DoesNotContain("IWorldStorage", client, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalWorldStorage", client, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveWorld", client, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveStateRevision", client, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveEnvironmentRevision", client, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage,\n                    GetAccessTokenAsync", runtime, StringComparison.Ordinal);
        Assert.Contains(
            "PrepareOwnedPrivateWorldMaterializationAsync",
            runtime,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClientUsesOnlyMaterializationReadyRouteAndValidatesBeforeCache()
    {
        var client = Read(
            "src/SharedWorlds.Infrastructure/Remote/StewardPrivateSnapshotMaterializationClient.cs");

        Assert.Contains(
            "api/v1/private-worlds/{worldId.Value:D}/materialization-download",
            client,
            StringComparison.Ordinal);
        Assert.DoesNotContain("/snapshot-download", client, StringComparison.Ordinal);
        var evidenceValidation = RequiredIndex(
            client,
            "evidence.ValidateAgainst(descriptor);");
        var authorizationValidation = RequiredIndex(
            client,
            "var authorization = Authorization.ToDomain(",
            evidenceValidation);
        var cachePublication = RequiredIndex(
            client,
            "var cached = await _cache.EnsureAsync(");

        Assert.True(evidenceValidation < authorizationValidation);
        Assert.True(authorizationValidation < cachePublication);
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
