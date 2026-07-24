using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SevenDaysToDie.Tests;

public sealed class SevenDaysToDieModEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-7dtd-mods-{Guid.NewGuid():N}");

    [Fact]
    public void InspectionReadsV2AndLegacyModInfoWithoutHashingModPayloads()
    {
        var installation = CreateInstallation("1000");
        WriteV2Mod("Author_NewMod", "2.4.1");
        WriteLegacyMod("LegacyMod", "1.2.0");
        var ignored = Path.Combine(ServerRoot(), "Mods", "NotAMod");
        Directory.CreateDirectory(ignored);
        File.WriteAllBytes(Path.Combine(ignored, "large-payload.bin"), [1, 2, 3]);

        var environment = SevenDaysToDieEnvironment.Inspect(installation);

        Assert.Equal("1000", environment.GameVersion);
        Assert.Equal(2, environment.Components.Count);
        Assert.Collection(
            environment.Components.OrderBy(component => component.Id, StringComparer.Ordinal),
            component =>
            {
                Assert.Equal("Author_NewMod", component.Id);
                Assert.Equal("2.4.1", component.Version);
                Assert.Equal("mod", component.Kind);
                Assert.Equal("dedicated-server", component.Source);
            },
            component =>
            {
                Assert.Equal("LegacyMod", component.Id);
                Assert.Equal("1.2.0", component.Version);
            });
    }

    [Fact]
    public void VerificationBlocksMissingUnexpectedAndVersionMismatchedMods()
    {
        var installation = CreateInstallation("1000");
        WriteV2Mod("InstalledWrongVersion", "2.0");
        WriteV2Mod("Unexpected", "1.0");

        var required = new EnvironmentManifest(
            1,
            "7-days-to-die",
            "1000",
            [
                new EnvironmentComponent("mod", "Missing", "1.0", "dedicated-server"),
                new EnvironmentComponent("mod", "InstalledWrongVersion", "1.0", "dedicated-server")
            ],
            new Dictionary<string, string>(StringComparer.Ordinal));

        var verification = SevenDaysToDieEnvironment.Verify(installation, required);

        Assert.False(verification.IsReady);
        Assert.Contains(verification.Issues, issue => issue.Code == "7dtd-mod-missing");
        Assert.Contains(verification.Issues, issue => issue.Code == "7dtd-mod-version-mismatch");
        Assert.Contains(verification.Issues, issue => issue.Code == "7dtd-unexpected-mod");
    }

    [Fact]
    public void VerificationBlocksMalformedRequiredModIdentitiesWithoutThrowing()
    {
        var installation = CreateInstallation("1000");
        WriteV2Mod("Duplicate", "1.0");

        var required = new EnvironmentManifest(
            1,
            "7-days-to-die",
            "1000",
            [
                new EnvironmentComponent("mod", "Duplicate", "1.0", "dedicated-server"),
                new EnvironmentComponent("mod", "Duplicate", "1.0", "dedicated-server"),
                new EnvironmentComponent("mod", string.Empty, "1.0", "dedicated-server")
            ],
            new Dictionary<string, string>(StringComparer.Ordinal));

        var verification = SevenDaysToDieEnvironment.Verify(installation, required);

        Assert.False(verification.IsReady);
        Assert.Contains(verification.Issues, issue => issue.Code == "7dtd-required-mod-duplicate");
        Assert.Contains(verification.Issues, issue => issue.Code == "7dtd-mod-id-invalid");
    }

    [Fact]
    public void LegacyModWithoutDeclaredVersionCanBeInspectedButNotClaimedExactOnAnotherHost()
    {
        var installation = CreateInstallation("1000");
        WriteLegacyMod("LegacyNoVersion", version: null);
        var required = SevenDaysToDieEnvironment.Inspect(installation);

        var verification = SevenDaysToDieEnvironment.Verify(installation, required);

        Assert.False(verification.IsReady);
        Assert.Contains(verification.Issues, issue => issue.Code == "7dtd-mod-version-unavailable");
    }

    [Fact]
    public void DuplicateDeclaredModIdentityFailsClosed()
    {
        var installation = CreateInstallation("1000");
        WriteV2Mod("Duplicate", "1.0", folder: "one");
        WriteV2Mod("Duplicate", "1.0", folder: "two");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SevenDaysToDieEnvironment.Inspect(installation));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedRecognizedModMetadataFailsClosed()
    {
        var installation = CreateInstallation("1000");
        var directory = Path.Combine(ServerRoot(), "Mods", "Broken");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ModInfo.xml"), "<xml><Name value=\"Broken\"></xml>");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SevenDaysToDieEnvironment.Inspect(installation));

        Assert.Contains("could not be read safely", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var serverRoot = ServerRoot();
        Directory.CreateDirectory(serverRoot);
        var manifest = Path.Combine(_root, "steamapps", "appmanifest_294420.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\"\n{{\n    \"installdir\" \"7 Days to Die Dedicated Server\"\n    \"buildid\" \"{buildId}\"\n}}");
        return new GameInstallation(
            "7-days-to-die:test",
            Path.Combine(_root, "client"),
            "steam",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SevenDaysToDieInstallationDiscovery.DedicatedServerRootPathKey] = serverRoot,
                [SevenDaysToDieInstallationDiscovery.DedicatedServerManifestPathKey] = manifest,
                [SevenDaysToDieInstallationDiscovery.DedicatedServerInstallStateKey] = "installed"
            });
    }

    private string ServerRoot()
        => Path.Combine(_root, "steamapps", "common", "7 Days to Die Dedicated Server");

    private void WriteV2Mod(string name, string version, string? folder = null)
    {
        var directory = Path.Combine(ServerRoot(), "Mods", folder ?? name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "ModInfo.xml"),
            $"<?xml version=\"1.0\"?><xml><Name value=\"{name}\"/><DisplayName value=\"{name}\"/><Version value=\"{version}\"/></xml>");
    }

    private void WriteLegacyMod(string name, string? version)
    {
        var directory = Path.Combine(ServerRoot(), "Mods", name);
        Directory.CreateDirectory(directory);
        var versionElement = version is null ? string.Empty : $"<Version value=\"{version}\"/>";
        File.WriteAllText(
            Path.Combine(directory, "ModInfo.xml"),
            $"<?xml version=\"1.0\"?><xml><ModInfo><Name value=\"{name}\"/>{versionElement}</ModInfo></xml>");
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
