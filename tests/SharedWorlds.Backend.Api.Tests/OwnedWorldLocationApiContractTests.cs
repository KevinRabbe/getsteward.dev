using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class OwnedWorldLocationApiContractTests
{
    [Fact]
    public void RoutesAreAuthenticatedAndDoNotAcceptOwnerIdentityFields()
    {
        var api = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Backend.Api/StewardOwnedWorldLocationApi.cs"));
        var program = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Backend.Api/Program.cs"));

        Assert.Contains("/api/v1/installations/current", api, StringComparison.Ordinal);
        Assert.Contains("/api/v1/private-worlds/{worldId:guid}/location", api, StringComparison.Ordinal);
        Assert.Contains("/api/v1/private-worlds/{worldId:guid}/bring-here", api, StringComparison.Ordinal);
        Assert.Contains("StewardApiResults.AuthenticationRequired()", api, StringComparison.Ordinal);
        Assert.Contains("StewardAuthenticatedCaller", api, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerProvider", api, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerExternalId", api, StringComparison.Ordinal);
        Assert.Contains("Results.Conflict(response)", api, StringComparison.Ordinal);
        Assert.Contains("app.MapStewardOwnedWorldLocationApiV1();", program, StringComparison.Ordinal);
        Assert.Contains("PostgreSqlOwnedWorldLocationStore", program, StringComparison.Ordinal);
        Assert.Contains("OwnedWorldLocationApplicationService", program, StringComparison.Ordinal);
        Assert.Contains("InitializeAsync()", program, StringComparison.Ordinal);
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
