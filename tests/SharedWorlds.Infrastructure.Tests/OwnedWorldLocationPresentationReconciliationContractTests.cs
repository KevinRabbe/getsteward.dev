using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class OwnedWorldLocationPresentationReconciliationContractTests
{
    [Fact]
    public void CanonicalCatalogSuppliesExactWorldNameAndAdapterToPublicationState()
    {
        var reconciler = Read(
            "src/SharedWorlds.Infrastructure/Remote/StewardOwnedWorldLocationCatalogReconciler.cs");
        var resolver = Read(
            "src/SharedWorlds.Infrastructure/Remote/OwnedWorldCanonicalSnapshotResolver.cs");

        Assert.Contains(
            "await _publication.RecordDesiredWithPresentationAsync(",
            reconciler,
            StringComparison.Ordinal);
        Assert.Contains(
            "world.Name,\n            world.GameAdapterId,",
            reconciler,
            StringComparison.Ordinal);
        Assert.Contains(
            "new OwnedWorldCanonicalSnapshotResolver(localStorage)",
            reconciler,
            StringComparison.Ordinal);
        Assert.Contains(
            "World '{world.Id}', state revision '{state.Id}', and environment revision '{environment.Id}' do not agree on one exact adapter ID.",
            resolver,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsRevisionPayloadAvailableAsync(",
            resolver,
            StringComparison.Ordinal);
        Assert.True(
            reconciler.IndexOf(
                "await _snapshotResolver.ResolveAsync(",
                StringComparison.Ordinal) <
            reconciler.IndexOf(
                "RecordDesiredWithPresentationAsync(",
                StringComparison.Ordinal));
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
