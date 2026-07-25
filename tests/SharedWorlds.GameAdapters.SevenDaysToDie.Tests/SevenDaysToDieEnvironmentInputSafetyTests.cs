using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.SevenDaysToDie.Tests;

public sealed class SevenDaysToDieEnvironmentInputSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-7dtd-environment-input-safety-{Guid.NewGuid():N}");

    [Fact]
    public void InspectionRejectsLinkedDedicatedServerRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var realServerRoot = Path.Combine(_root, "real-server");
        Directory.CreateDirectory(realServerRoot);
        var linkedServerRoot = Path.Combine(_root, "linked-server");
        Directory.CreateSymbolicLink(linkedServerRoot, realServerRoot);
        var installation = CreateInstallation(linkedServerRoot, CreateManifest("1000"));
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                SevenDaysToDieEnvironment.Inspect(installation));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Dedicated Server root", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(linkedServerRoot);
        }
    }

    [Fact]
    public void InspectionRejectsLinkedSteamManifest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = CreateServerRoot();
        var outsideManifest = Path.Combine(_root, "outside-appmanifest.acf");
        File.WriteAllText(outsideManifest, ManifestText("1000"));
        var linkedManifest = Path.Combine(_root, "linked-appmanifest.acf");
        File.CreateSymbolicLink(linkedManifest, outsideManifest);
        var installation = CreateInstallation(serverRoot, linkedManifest);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                SevenDaysToDieEnvironment.Inspect(installation));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Steam manifest", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(linkedManifest);
        }
    }

    [Fact]
    public void InspectionRejectsLinkedModsRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = CreateServerRoot();
        var outsideMods = Path.Combine(_root, "outside-mods");
        Directory.CreateDirectory(outsideMods);
        WriteMod(outsideMods, "OutsideMod", "1.0");
        var linkedMods = Path.Combine(serverRoot, "Mods");
        Directory.CreateSymbolicLink(linkedMods, outsideMods);
        var installation = CreateInstallation(serverRoot, CreateManifest("1000"));
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                SevenDaysToDieEnvironment.Inspect(installation));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Mods directory", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(linkedMods);
        }
    }

    [Fact]
    public void InspectionRejectsLinkedModDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = CreateServerRoot();
        var modsRoot = Path.Combine(serverRoot, "Mods");
        Directory.CreateDirectory(modsRoot);
        var outsideMod = Path.Combine(_root, "outside-mod");
        Directory.CreateDirectory(outsideMod);
        File.WriteAllText(Path.Combine(outsideMod, "ModInfo.xml"), ModInfo("OutsideMod", "1.0"));
        var linkedMod = Path.Combine(modsRoot, "LinkedMod");
        Directory.CreateSymbolicLink(linkedMod, outsideMod);
        var installation = CreateInstallation(serverRoot, CreateManifest("1000"));
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                SevenDaysToDieEnvironment.Inspect(installation));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("mod directory", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(linkedMod);
        }
    }

    [Fact]
    public void InspectionRejectsLinkedModInfo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = CreateServerRoot();
        var modRoot = Path.Combine(serverRoot, "Mods", "LinkedMetadata");
        Directory.CreateDirectory(modRoot);
        var outsideModInfo = Path.Combine(_root, "outside-ModInfo.xml");
        File.WriteAllText(outsideModInfo, ModInfo("LinkedMetadata", "1.0"));
        var linkedModInfo = Path.Combine(modRoot, "ModInfo.xml");
        File.CreateSymbolicLink(linkedModInfo, outsideModInfo);
        var installation = CreateInstallation(serverRoot, CreateManifest("1000"));
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                SevenDaysToDieEnvironment.Inspect(installation));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ModInfo.xml", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(linkedModInfo);
        }
    }

    [Fact]
    public void InspectionRejectsOversizedSteamManifestBeforeRead()
    {
        var serverRoot = CreateServerRoot();
        var manifest = Path.Combine(_root, "steamapps", "oversized-appmanifest.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        using (var stream = new FileStream(
                   manifest,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(SevenDaysToDieEnvironment.MaximumSteamManifestBytes + 1L);
        }

        var installation = CreateInstallation(serverRoot, manifest);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SevenDaysToDieEnvironment.Inspect(installation));

        Assert.Contains("Steam manifest", exception.Message, StringComparison.Ordinal);
        Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            SevenDaysToDieEnvironment.MaximumSteamManifestBytes + 1L,
            new FileInfo(manifest).Length);
    }

    [Fact]
    public void InspectionRejectsOversizedModInfoBeforeXmlLoad()
    {
        var serverRoot = CreateServerRoot();
        var modRoot = Path.Combine(serverRoot, "Mods", "OversizedMetadata");
        Directory.CreateDirectory(modRoot);
        var modInfoPath = Path.Combine(modRoot, "ModInfo.xml");
        using (var stream = new FileStream(
                   modInfoPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(SevenDaysToDieEnvironment.MaximumModInfoBytes + 1L);
        }

        var installation = CreateInstallation(serverRoot, CreateManifest("1000"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SevenDaysToDieEnvironment.Inspect(installation));

        Assert.Contains("ModInfo.xml", exception.Message, StringComparison.Ordinal);
        Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            SevenDaysToDieEnvironment.MaximumModInfoBytes + 1L,
            new FileInfo(modInfoPath).Length);
    }

    [Fact]
    public void InspectionRejectsExcessiveTopLevelModDirectoryCount()
    {
        var serverRoot = CreateServerRoot();
        var modsRoot = Path.Combine(serverRoot, "Mods");
        Directory.CreateDirectory(modsRoot);
        for (var index = 0; index <= SevenDaysToDieEnvironment.MaximumModDirectories; index++)
        {
            Directory.CreateDirectory(Path.Combine(modsRoot, $"mod-{index:D4}"));
        }

        var installation = CreateInstallation(serverRoot, CreateManifest("1000"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SevenDaysToDieEnvironment.Inspect(installation));

        Assert.Contains("Mods directory", exception.Message, StringComparison.Ordinal);
        Assert.Contains("directory environment inventory safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectionStillAcceptsRegularManifestRootAndModMetadata()
    {
        var serverRoot = CreateServerRoot();
        var modsRoot = Path.Combine(serverRoot, "Mods");
        Directory.CreateDirectory(modsRoot);
        WriteMod(modsRoot, "RegularMod", "2.0");
        var installation = CreateInstallation(serverRoot, CreateManifest("1000"));

        var environment = SevenDaysToDieEnvironment.Inspect(installation);

        Assert.Equal("1000", environment.GameVersion);
        var mod = Assert.Single(environment.Components);
        Assert.Equal("RegularMod", mod.Id);
        Assert.Equal("2.0", mod.Version);
    }

    private string CreateServerRoot()
    {
        var serverRoot = Path.Combine(_root, "server");
        Directory.CreateDirectory(serverRoot);
        return serverRoot;
    }

    private string CreateManifest(string buildId)
    {
        var manifest = Path.Combine(_root, "steamapps", "appmanifest_294420.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, ManifestText(buildId));
        return manifest;
    }

    private static string ManifestText(string buildId)
        => $"\"AppState\"\n{{\n    \"installdir\" \"7 Days to Die Dedicated Server\"\n    \"buildid\" \"{buildId}\"\n}}";

    private static void WriteMod(
        string modsRoot,
        string name,
        string version)
    {
        var modRoot = Path.Combine(modsRoot, name);
        Directory.CreateDirectory(modRoot);
        File.WriteAllText(Path.Combine(modRoot, "ModInfo.xml"), ModInfo(name, version));
    }

    private static string ModInfo(string name, string version)
        => $"<?xml version=\"1.0\"?><xml><Name value=\"{name}\"/><Version value=\"{version}\"/></xml>";

    private static GameInstallation CreateInstallation(
        string serverRoot,
        string manifestPath)
        => new(
            "7-days-to-die:test",
            serverRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SevenDaysToDieInstallationDiscovery.DedicatedServerRootPathKey] = serverRoot,
                [SevenDaysToDieInstallationDiscovery.DedicatedServerManifestPathKey] = manifestPath,
                [SevenDaysToDieInstallationDiscovery.DedicatedServerInstallStateKey] = "installed"
            });

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
