using SharedWorlds.Core.Abstractions;
using SharedWorlds.GameAdapters.Astroneer;
using Xunit;

namespace SharedWorlds.GameAdapters.Astroneer.Tests;

public sealed class AstroneerInstalledModGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-astroneer-install-mod-{Guid.NewGuid():N}");

    [Fact]
    public void CleanCurrentInstallRootPasses()
    {
        var installationRoot = CreateInstallRoot();
        var paksRoot = Path.Combine(installationRoot, "Astro", "Content", "Paks");
        Directory.CreateDirectory(paksRoot);
        File.WriteAllBytes(
            Path.Combine(paksRoot, AstroneerInstalledModGuard.StockClientPakName),
            [1]);

        AstroneerInstalledModGuard.RequireNoInstallRootMods(installationRoot);
    }

    [Fact]
    public void Ue4SsDirectoryBlocksVanillaProof()
    {
        var installationRoot = CreateInstallRoot();
        Directory.CreateDirectory(Path.Combine(
            installationRoot,
            "Astro",
            "Binaries",
            "Win64",
            "ue4ss",
            "Mods"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AstroneerInstalledModGuard.RequireNoInstallRootMods(installationRoot));

        Assert.Contains("UE4SS marker 'ue4ss'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("vanilla-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ue4SsBootstrapDllBlocksVanillaProof()
    {
        var installationRoot = CreateInstallRoot();
        var win64 = Path.Combine(installationRoot, "Astro", "Binaries", "Win64");
        Directory.CreateDirectory(win64);
        File.WriteAllBytes(Path.Combine(win64, "dwmapi.dll"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AstroneerInstalledModGuard.RequireNoInstallRootMods(installationRoot));

        Assert.Contains("dwmapi.dll", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectExtraPakBlocksVanillaProof()
    {
        var installationRoot = CreateInstallRoot();
        var paksRoot = Path.Combine(installationRoot, "Astro", "Content", "Paks");
        Directory.CreateDirectory(paksRoot);
        File.WriteAllBytes(
            Path.Combine(paksRoot, AstroneerInstalledModGuard.StockClientPakName),
            [1]);
        File.WriteAllBytes(Path.Combine(paksRoot, "BiggerBackpack_P.pak"), [2]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AstroneerInstalledModGuard.RequireNoInstallRootMods(installationRoot));

        Assert.Contains("BiggerBackpack_P.pak", exception.Message, StringComparison.Ordinal);
        Assert.Contains("vanilla-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownStockLikePakBlocksVanillaProof()
    {
        var installationRoot = CreateInstallRoot();
        var paksRoot = Path.Combine(installationRoot, "Astro", "Content", "Paks");
        Directory.CreateDirectory(paksRoot);
        File.WriteAllBytes(
            Path.Combine(paksRoot, AstroneerInstalledModGuard.StockClientPakName),
            [1]);
        File.WriteAllBytes(Path.Combine(paksRoot, "pakchunk1-WindowsNoEditor.pak"), [2]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AstroneerInstalledModGuard.RequireNoInstallRootMods(installationRoot));

        Assert.Contains("pakchunk1-WindowsNoEditor.pak", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkedStockPakBlocksVanillaProof()
    {
        var installationRoot = CreateInstallRoot();
        var paksRoot = Path.Combine(installationRoot, "Astro", "Content", "Paks");
        Directory.CreateDirectory(paksRoot);
        var outside = Path.Combine(_root, "outside.pak");
        File.WriteAllBytes(outside, [1]);
        var linked = Path.Combine(paksRoot, AstroneerInstalledModGuard.StockClientPakName);

        try
        {
            File.CreateSymbolicLink(linked, outside);
        }
        catch (Exception linkException) when (
            linkException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AstroneerInstalledModGuard.RequireNoInstallRootMods(installationRoot));

        Assert.Contains("linked", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnvironmentInspectionRejectsInstallRootModWithEmptySavedModRoots()
    {
        var installation = CreateInstallation("24680");
        Directory.CreateDirectory(Path.Combine(
            installation.RootPath,
            "Astro",
            "Binaries",
            "Win64",
            "ue4ss",
            "Mods",
            "ExampleMod"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AstroneerEnvironment.Inspect(installation));

        Assert.Contains("UE4SS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnvironmentInspectionAcceptsCurrentStockInstallRoot()
    {
        var installation = CreateInstallation("24680");
        var paksRoot = Path.Combine(installation.RootPath, "Astro", "Content", "Paks");
        Directory.CreateDirectory(paksRoot);
        File.WriteAllBytes(
            Path.Combine(paksRoot, AstroneerInstalledModGuard.StockClientPakName),
            [1]);

        var environment = AstroneerEnvironment.Inspect(installation);

        Assert.Equal("24680", environment.GameVersion);
    }

    private string CreateInstallRoot()
    {
        var installationRoot = Path.Combine(_root, $"install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(installationRoot);
        return installationRoot;
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, $"library-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(library, "steamapps", "common", "ASTRONEER");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Astro.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_361420.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var savedRoot = Path.Combine(_root, $"saved-{Guid.NewGuid():N}");

        return new GameInstallation(
            $"astroneer:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AstroneerInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "Astro.exe"),
                [AstroneerInstallationDiscovery.SteamManifestPathKey] = manifest,
                [AstroneerInstallationDiscovery.WorldRootPathKey] = Path.Combine(
                    savedRoot,
                    "SaveGames"),
                [AstroneerInstallationDiscovery.ModsRootPathKey] = Path.Combine(
                    savedRoot,
                    "Mods"),
                [AstroneerInstallationDiscovery.PaksRootPathKey] = Path.Combine(
                    savedRoot,
                    "Paks"),
                [AstroneerInstallationDiscovery.GameSteamAppIdKey] = AstroneerInstallationDiscovery.GameSteamAppId
            });
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
