using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class OwnedPrivateWorldCatalogDesktopProjectionTests
{
    [Fact]
    public void CatalogReusesTheAuthenticatedRuntimeSessionAndHttpClient()
    {
        var runtime = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/StewardDesktopRemoteRuntime.cs"));

        Assert.Equal(1, CountOccurrences(runtime, "new StewardAccessSession("));
        Assert.Equal(
            1,
            CountOccurrences(
                runtime,
                "new StewardOwnedPrivateWorldCatalogClient("));
        Assert.Contains(
            "var ownedWorldLocations = new StewardOwnedWorldLocationClient(\n                apiClient,\n                GetAccessTokenAsync);",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "var ownedPrivateWorldCatalog = new StewardOwnedPrivateWorldCatalogClient(\n                apiClient,\n                GetAccessTokenAsync);",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "=> _ownedPrivateWorldCatalog.ListAsync(cancellationToken);",
            runtime,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteOnlyWorldsUseSeparateReadOnlyPresentationOwnership()
    {
        var projection = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.OwnedPrivateWorldCatalog.cs"));

        Assert.Contains(
            "gamesHome.Children.Insert(gameLibraryIndex + 1, _ownedPrivateWorldSection);",
            projection,
            StringComparison.Ordinal);
        Assert.Contains(
            "detailsRoot.Children.Add(_ownedPrivateWorldDetailsPanel);",
            projection,
            StringComparison.Ordinal);
        Assert.Contains(
            "private readonly List<OwnedPrivateWorldListItem> _ownedPrivateWorldItems = [];",
            projection,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WorldList.ItemsSource = _ownedPrivateWorldItems",
            projection,
            StringComparison.Ordinal);
        Assert.Contains("_selectedWorld = null;", projection, StringComparison.Ordinal);
        Assert.Contains("_selectedGameAdapterId = null;", projection, StringComparison.Ordinal);
        Assert.Contains(
            "This World is not stored on this PC. Safe World will not start, host, share, or delete it from this read-only view.",
            projection,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PrimaryActionButton", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("HostButton", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("ShareWorldButton", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteWorldButton", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectionFiltersMaterializedAndAlreadyHereWorlds()
    {
        var projection = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.OwnedPrivateWorldCatalog.cs"));

        Assert.Contains(
            "var materializedWorldIds = (await _storage.ListWorldsAsync(cancellationToken))",
            projection,
            StringComparison.Ordinal);
        Assert.Contains(
            "materializedWorldIds.Contains(entry.WorldId) ||\n                    entry.Availability == BringHereAvailability.AlreadyHere",
            projection,
            StringComparison.Ordinal);
        Assert.Contains(
            "entry.GameAdapterId is null",
            projection,
            StringComparison.Ordinal);
        Assert.Contains(
            "Game identity conflict",
            projection,
            StringComparison.Ordinal);
        Assert.Contains(
            "No source selected",
            projection,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticationRefreshesMaterializedWorldsBeforeRemoteOnlyCatalog()
    {
        var composition = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs"));

        var swap = RequiredIndex(
            composition,
            "previous = Interlocked.Exchange(ref _remoteRuntime, next);");
        var unified = RequiredIndex(
            composition,
            "await RefreshUnifiedWorldsAsync(",
            swap);
        var catalog = RequiredIndex(
            composition,
            "await RefreshOwnedPrivateWorldCatalogAsync(cancellationToken);",
            unified);
        var cancellation = RequiredIndex(
            composition,
            "cancellationToken.ThrowIfCancellationRequested();",
            catalog);

        Assert.True(swap < unified);
        Assert.True(unified < catalog);
        Assert.True(catalog < cancellation);
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
