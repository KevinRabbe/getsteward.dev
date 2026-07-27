using System.Text.Json;
using SharedWorlds.GameAdapters.CoreKeeper;
using Xunit;

namespace SharedWorlds.GameAdapters.CoreKeeper.Tests;

public sealed class CoreKeeperModIoGuardTests
{
    [Fact]
    public void EmptyKnownRootsRemainVanilla()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.CreateDirectory("default-modio");
        var settingsRoot = temp.CreateDirectory("settings-modio");

        CoreKeeperModIoGuard.RequireNoInstalledMods(defaultRoot, settingsRoot);
    }

    [Fact]
    public void DefaultModIoPayloadBlocksVanillaProof()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.CreateDirectory("default-modio");
        var settingsRoot = temp.CreateDirectory("settings-modio");
        var modsRoot = Directory.CreateDirectory(
            Path.Combine(defaultRoot, CoreKeeperModIoGuard.ModIoGameId, "mods")).FullName;
        Directory.CreateDirectory(Path.Combine(modsRoot, "123456"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => CoreKeeperModIoGuard.RequireNoInstalledMods(defaultRoot, settingsRoot));

        Assert.Contains("official mod.io content directory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalStorageRedirectIsInspected()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.CreateDirectory("default-modio");
        var settingsRoot = temp.CreateDirectory("settings-modio");
        var redirectedRoot = temp.CreateDirectory("redirected-modio");
        File.WriteAllText(
            Path.Combine(settingsRoot, "globalsettings.json"),
            JsonSerializer.Serialize(new { RootLocalStoragePath = redirectedRoot }));
        var modsRoot = Directory.CreateDirectory(
            Path.Combine(redirectedRoot, CoreKeeperModIoGuard.ModIoGameId, "mods")).FullName;
        File.WriteAllText(Path.Combine(modsRoot, "installed-mod.marker"), "mod");

        var exception = Assert.Throws<InvalidOperationException>(
            () => CoreKeeperModIoGuard.RequireNoInstalledMods(defaultRoot, settingsRoot));

        Assert.Contains("official mod.io content directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PerProfileStorageRedirectIsInspected()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.CreateDirectory("default-modio");
        var settingsRoot = temp.CreateDirectory("settings-modio");
        var redirectedRoot = temp.CreateDirectory("profile-modio");
        var profileRoot = Directory.CreateDirectory(
            Path.Combine(settingsRoot, CoreKeeperModIoGuard.ModIoGameId, "local-profile")).FullName;
        File.WriteAllText(
            Path.Combine(profileRoot, "user.json"),
            JsonSerializer.Serialize(new { RootLocalStoragePath = redirectedRoot }));
        var modsRoot = Directory.CreateDirectory(
            Path.Combine(redirectedRoot, CoreKeeperModIoGuard.ModIoGameId, "mods")).FullName;
        Directory.CreateDirectory(Path.Combine(modsRoot, "987654"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => CoreKeeperModIoGuard.RequireNoInstalledMods(defaultRoot, settingsRoot));

        Assert.Contains("official mod.io content directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedStorageSettingsFailClosed()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.CreateDirectory("default-modio");
        var settingsRoot = temp.CreateDirectory("settings-modio");
        File.WriteAllText(Path.Combine(settingsRoot, "globalsettings.json"), "{not-json");

        var exception = Assert.Throws<InvalidOperationException>(
            () => CoreKeeperModIoGuard.RequireNoInstalledMods(defaultRoot, settingsRoot));

        Assert.Contains("could not parse mod.io global storage settings safely", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DuplicateStorageRedirectFailsClosed()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.CreateDirectory("default-modio");
        var settingsRoot = temp.CreateDirectory("settings-modio");
        File.WriteAllText(
            Path.Combine(settingsRoot, "globalsettings.json"),
            "{\"RootLocalStoragePath\":\"C:/one\",\"RootLocalStoragePath\":\"C:/two\"}");

        var exception = Assert.Throws<InvalidOperationException>(
            () => CoreKeeperModIoGuard.RequireNoInstalledMods(defaultRoot, settingsRoot));

        Assert.Contains("ambiguous or invalid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelativeStorageRedirectFailsClosed()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.CreateDirectory("default-modio");
        var settingsRoot = temp.CreateDirectory("settings-modio");
        File.WriteAllText(
            Path.Combine(settingsRoot, "globalsettings.json"),
            "{\"RootLocalStoragePath\":\"relative/mod.io\"}");

        var exception = Assert.Throws<InvalidOperationException>(
            () => CoreKeeperModIoGuard.RequireNoInstalledMods(defaultRoot, settingsRoot));

        Assert.Contains("not an absolute path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OversizedStorageSettingsFailClosed()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.CreateDirectory("default-modio");
        var settingsRoot = temp.CreateDirectory("settings-modio");
        File.WriteAllBytes(
            Path.Combine(settingsRoot, "globalsettings.json"),
            new byte[checked((int)CoreKeeperModIoGuard.MaximumSettingsBytes + 1)]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => CoreKeeperModIoGuard.RequireNoInstalledMods(defaultRoot, settingsRoot));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsRootOccupiedByFileFailsClosed()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.CreateDirectory("default-modio");
        var settingsRoot = Path.Combine(temp.Path, "settings-modio");
        File.WriteAllText(settingsRoot, "not-a-directory");

        var exception = Assert.Throws<InvalidOperationException>(
            () => CoreKeeperModIoGuard.RequireNoInstalledMods(defaultRoot, settingsRoot));

        Assert.Contains("settings root", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a regular directory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinkedRedirectedStorageRootFailsClosed()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.CreateDirectory("default-modio");
        var settingsRoot = temp.CreateDirectory("settings-modio");
        var outsideRoot = temp.CreateDirectory("outside-modio");
        var linkedRoot = Path.Combine(temp.Path, "linked-modio");

        try
        {
            Directory.CreateSymbolicLink(linkedRoot, outsideRoot);
        }
        catch (Exception linkException) when (linkException is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        File.WriteAllText(
            Path.Combine(settingsRoot, "globalsettings.json"),
            JsonSerializer.Serialize(new { RootLocalStoragePath = linkedRoot }));

        var exception = Assert.Throws<InvalidOperationException>(
            () => CoreKeeperModIoGuard.RequireNoInstalledMods(defaultRoot, settingsRoot));

        Assert.Contains("storage root", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("linked", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TooManyLocalProfilesFailClosed()
    {
        using var temp = new TempDirectory();
        var defaultRoot = temp.CreateDirectory("default-modio");
        var settingsRoot = temp.CreateDirectory("settings-modio");
        var gameSettingsRoot = Directory.CreateDirectory(
            Path.Combine(settingsRoot, CoreKeeperModIoGuard.ModIoGameId)).FullName;

        for (var index = 0; index <= CoreKeeperModIoGuard.MaximumLocalProfiles; index++)
        {
            Directory.CreateDirectory(Path.Combine(gameSettingsRoot, index.ToString("D3")));
        }

        var exception = Assert.Throws<InvalidOperationException>(
            () => CoreKeeperModIoGuard.RequireNoInstalledMods(defaultRoot, settingsRoot));

        Assert.Contains("local-profile safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"steward-core-keeper-modio-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(string relativePath)
            => Directory.CreateDirectory(System.IO.Path.Combine(Path, relativePath)).FullName;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }
}
