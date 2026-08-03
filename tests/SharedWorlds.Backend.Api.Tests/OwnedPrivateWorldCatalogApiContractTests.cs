using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class OwnedPrivateWorldCatalogApiContractTests
{
    [Fact]
    public void CatalogRouteIsAuthenticatedOwnerScopedAndUsesIntegerAvailabilityWire()
    {
        var api = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Backend.Api/StewardOwnedWorldLocationApi.cs"));

        Assert.Contains(
            "endpoints.MapGet(\"/api/v1/private-worlds\", ListPrivateWorldCatalogAsync);",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "var caller = await AuthenticateAsync(request, sessions, cancellationToken);",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "new PostgreSqlOwnedWorldLocationCatalogStore(dataSource)",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "new OwnedPrivateWorldCatalogApplicationService(",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"OwnedPrivateWorldCatalog\"",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "(int)entry.Availability",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "(int)decision.Availability",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "int Availability",
            api,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ownerProvider", api, StringComparison.Ordinal);
        Assert.DoesNotContain("ownerExternalId", api, StringComparison.Ordinal);
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
