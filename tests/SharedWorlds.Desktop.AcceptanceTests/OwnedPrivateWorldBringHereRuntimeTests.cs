using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class OwnedPrivateWorldBringHereRuntimeTests
{
    [Fact]
    public void RuntimeComposesOneWriterFromObservedStorageAndVerifiedCache()
    {
        var runtime = Read("src/SharedWorlds.Desktop/StewardDesktopRemoteRuntime.cs");
        var mainWindow = Read("src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs");

        Assert.Equal(
            1,
            CountOccurrences(
                runtime,
                "new StewardOwnedPrivateWorldMaterializationService("));
        Assert.Contains(
            "new StewardOwnedPrivateWorldMaterializationService(\n                    localStorage,\n                    verifiedCache);",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "var migrationPublication = EnsureOwnedWorldLocationMigrationState();",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "remoteRoot,\n            _storage,\n            migrationPublication.Journal,",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(runtime, "new VerifiedPackageCache("));
        Assert.Equal(1, CountOccurrences(runtime, "new StewardAccessSession("));
    }

    [Fact]
    public void OrchestrationRelistsThenPreparesThenCommitsOneExactEntry()
    {
        var runtime = Read("src/SharedWorlds.Desktop/StewardDesktopRemoteRuntime.cs");
        var methodStart = RequiredIndex(
            runtime,
            "BringOwnedPrivateWorldHereAsync(");
        var methodEnd = RequiredIndex(
            runtime,
            "public async Task ReconcileAndReplayOwnedWorldLocationsAsync(",
            methodStart);
        var method = runtime[methodStart..methodEnd];

        var catalog = RequiredIndex(
            method,
            "await _ownedPrivateWorldCatalog.ListAsync(cancellationToken)");
        var exactCount = RequiredIndex(
            method,
            "if (matchingEntries.Length != 1)",
            catalog);
        var availability = RequiredIndex(
            method,
            "if (entry.Availability != BringHereAvailability.Available",
            exactCount);
        var prepare = RequiredIndex(
            method,
            "await _privateSnapshotMaterialization.EnsureDownloadedAsync(",
            availability);
        var materialize = RequiredIndex(
            method,
            "return await _ownedPrivateWorldMaterialization.MaterializeAsync(",
            prepare);

        Assert.True(catalog < exactCount);
        Assert.True(exactCount < availability);
        Assert.True(availability < prepare);
        Assert.True(prepare < materialize);
        Assert.Contains(
            "entry,\n            prepared,\n            User,\n            cancellationToken",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OrchestrationDoesNotPublishLocationOrMutateUiDirectly()
    {
        var runtime = Read("src/SharedWorlds.Desktop/StewardDesktopRemoteRuntime.cs");
        var methodStart = RequiredIndex(
            runtime,
            "BringOwnedPrivateWorldHereAsync(");
        var methodEnd = RequiredIndex(
            runtime,
            "public async Task ReconcileAndReplayOwnedWorldLocationsAsync(",
            methodStart);
        var method = runtime[methodStart..methodEnd];

        Assert.DoesNotContain("OwnedWorldLocations", method, StringComparison.Ordinal);
        Assert.DoesNotContain("_ownedWorldLocationPublication", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ReconcileAndReplay", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshUnifiedWorlds", method, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Button", method, StringComparison.Ordinal);
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
