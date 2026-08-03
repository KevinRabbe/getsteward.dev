using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class OwnedWorldLocationPresentationReconciliationContractTests
{
    [Fact]
    public void CanonicalCatalogSuppliesExactWorldNameAndAdapterToPublicationState()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Infrastructure/Remote/StewardOwnedWorldLocationCatalogReconciler.cs"));

        Assert.Contains(
            "await _publication.RecordDesiredWithPresentationAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "world.Name,\n            world.GameAdapterId,",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "World '{world.Id}', state revision '{state.Id}', and environment revision '{environment.Id}' do not agree on one exact adapter ID.",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.IndexOf(
                "IsRevisionPayloadAvailableAsync(",
                StringComparison.Ordinal) <
            source.IndexOf(
                "RecordDesiredWithPresentationAsync(",
                StringComparison.Ordinal));
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
