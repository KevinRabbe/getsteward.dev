using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.Necesse;
using Xunit;

namespace SharedWorlds.GameAdapters.Necesse.Tests;

public sealed class NecesseWorkshopGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-necesse-workshop-{Guid.NewGuid():N}");

    [Fact]
    public void MissingWorkshopRootDoesNotBlockVanillaProof()
    {
        var installation = CreateInstallation();

        NecesseWorkshopGuard.RequireNoAmbiguousWorkshopContent(installation);
    }

    [Fact]
    public void EmptyWorkshopRootDoesNotBlockVanillaProof()
    {
        var installation = CreateInstallation();
        Directory.CreateDirectory(GetWorkshopContentRoot(installation));

        NecesseWorkshopGuard.RequireNoAmbiguousWorkshopContent(installation);
    }

    [Fact]
    public void InstalledWorkshopPayloadFailsClosedWithoutClaimingItIsActive()
    {
        var installation = CreateInstallation();
        var workshopRoot = GetWorkshopContentRoot(installation);
        Directory.CreateDirectory(Path.Combine(workshopRoot, "1234567890"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => NecesseWorkshopGuard.RequireNoAmbiguousWorkshopContent(installation));

        Assert.Contains("installed Steam Workshop payload", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot prove", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not being claimed as active", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkshopRootOccupiedByFileFailsClosed()
    {
        var installation = CreateInstallation();
        var workshopRoot = GetWorkshopContentRoot(installation);
        Directory.CreateDirectory(Path.GetDirectoryName(workshopRoot)!);
        File.WriteAllText(workshopRoot, "not-a-directory");

        var exception = Assert.Throws<InvalidOperationException>(
            () => NecesseWorkshopGuard.RequireNoAmbiguousWorkshopContent(installation));

        Assert.Contains("not a regular directory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinkedWorkshopRootFailsClosed()
    {
        var installation = CreateInstallation();
        var workshopRoot = GetWorkshopContentRoot(installation);
        var outsideRoot = Path.Combine(_root, "outside-workshop");
        Directory.CreateDirectory(outsideRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(workshopRoot)!);

        try
        {
            Directory.CreateSymbolicLink(workshopRoot, outsideRoot);
        }
        catch (Exception linkException) when (
            linkException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var exception = Assert.Throws<InvalidOperationException>(
            () => NecesseWorkshopGuard.RequireNoAmbiguousWorkshopContent(installation));

        Assert.Contains("linked", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnvironmentInspectionRejectsWorkshopPayloadBeforeLocalModStateExists()
    {
        var installation = CreateInstallation();
        var workshopRoot = GetWorkshopContentRoot(installation);
        Directory.CreateDirectory(Path.Combine(workshopRoot, "1234567890"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => NecesseEnvironment.Inspect(installation));

        Assert.Contains("Steam Workshop payload", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot prove", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private GameInstallation CreateInstallation()
    {
        var steamAppsRoot = Path.Combine(_root, $"steamapps-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(steamAppsRoot, "common", "Necesse");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Necesse.exe"), [1]);

        var manifest = Path.Combine(
            steamAppsRoot,
            $"appmanifest_{NecesseInstallationDiscovery.GameSteamAppId}.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "\"AppState\" { \"buildid\" \"24680\" }");

        var dataRoot = Path.Combine(_root, $"data-{Guid.NewGuid():N}");
        return new GameInstallation(
            $"necesse:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NecesseInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(installRoot, "Necesse.exe"),
                [NecesseInstallationDiscovery.SteamManifestPathKey] = manifest,
                [NecesseInstallationDiscovery.WorldRootPathKey] = Path.Combine(dataRoot, "saves", "worlds"),
                [NecesseInstallationDiscovery.ModsRootPathKey] = Path.Combine(dataRoot, "mods"),
                [NecesseInstallationDiscovery.GameSteamAppIdKey] = NecesseInstallationDiscovery.GameSteamAppId
            });
    }

    private static string GetWorkshopContentRoot(GameInstallation installation)
    {
        var manifest = installation.Metadata![NecesseInstallationDiscovery.SteamManifestPathKey];
        var steamAppsRoot = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
        return Path.Combine(
            steamAppsRoot,
            "workshop",
            "content",
            NecesseInstallationDiscovery.GameSteamAppId);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Test cleanup is best-effort.
        }
    }
}
