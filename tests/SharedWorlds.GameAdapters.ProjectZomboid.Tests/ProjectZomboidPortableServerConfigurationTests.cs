using System.IO.Compression;
using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidPortableServerConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-pz-portable-config-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void SanitizerBlanksOnlyLocalManagementSecrets()
    {
        const string source =
            "PublicName=Steward\r\n" +
            "Password=join-secret\r\n" +
            "RCONPassword=admin-secret\r\n" +
            "DiscordToken=discord-secret\r\n" +
            "# RCONPassword=comment-only\r\n";

        var sanitized = ProjectZomboidPortableServerConfiguration.SanitizeText(source);

        Assert.Contains("Password=join-secret\r\n", sanitized, StringComparison.Ordinal);
        Assert.Contains("RCONPassword=\r\n", sanitized, StringComparison.Ordinal);
        Assert.Contains("DiscordToken=\r\n", sanitized, StringComparison.Ordinal);
        Assert.Contains("# RCONPassword=comment-only\r\n", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("admin-secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("discord-secret", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureScrubsPortableConfigWithoutChangingLiveServerDefinition()
    {
        var userData = CreateServerBundle("steward");
        var configPath = Path.Combine(userData, "Server", "steward.ini");
        const string liveConfig =
            "PublicName=Steward\n" +
            "Password=join-secret\n" +
            "RCONPort=27015\n" +
            "RCONPassword=admin-secret\n" +
            "DiscordToken=discord-secret\n";
        await File.WriteAllTextAsync(configPath, liveConfig);
        var worldPath = Path.Combine(userData, "Saves", "Multiplayer", "steward");

        var captured = await ProjectZomboidWorldState.CaptureDetectedWorldAsync(
            Installation(userData),
            new DetectedWorld(worldPath, "steward", worldPath),
            CancellationToken.None);
        _packages.Add(captured.Package.Path);

        using var archive = ZipFile.OpenRead(captured.Package.Path);
        var entry = Assert.Single(
            archive.Entries,
            entry => string.Equals(
                entry.FullName,
                "Server/steward.ini",
                StringComparison.Ordinal));
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var portableConfig = await reader.ReadToEndAsync();
        Assert.Contains("Password=join-secret", portableConfig, StringComparison.Ordinal);
        Assert.Contains("RCONPort=27015", portableConfig, StringComparison.Ordinal);
        Assert.Contains("RCONPassword=\n", portableConfig, StringComparison.Ordinal);
        Assert.Contains("DiscordToken=\n", portableConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("admin-secret", portableConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("discord-secret", portableConfig, StringComparison.Ordinal);
        Assert.Equal(liveConfig, await File.ReadAllTextAsync(configPath));
    }

    [Fact]
    public async Task RestoreScrubsManagementSecretsFromLegacyPackageBeforePublishingWorkspace()
    {
        Directory.CreateDirectory(_root);
        var packagePath = Path.Combine(_root, "legacy-with-secrets.zip");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            await WriteBytesAsync(archive, "Saves/Multiplayer/steward/map_t.bin", [1, 2, 3]);
            await WriteTextAsync(
                archive,
                "Server/steward.ini",
                "PublicName=Steward\nPassword=join-secret\nRCONPassword=legacy-admin-secret\nDiscordToken=legacy-discord-secret\n");
        }

        var destination = Path.Combine(_root, "prepared", "Zomboid");
        var prepared = new PreparedWorld(
            Installation(Path.Combine(_root, "unused")),
            destination,
            EmptyEnvironment());

        await ProjectZomboidWorldState.RestorePreparedWorldAsync(
            prepared,
            new StatePackage("legacy", packagePath),
            CancellationToken.None);

        var restoredConfig = await File.ReadAllTextAsync(
            Path.Combine(destination, "Server", "steward.ini"));
        Assert.Contains("Password=join-secret", restoredConfig, StringComparison.Ordinal);
        Assert.Contains("RCONPassword=\n", restoredConfig, StringComparison.Ordinal);
        Assert.Contains("DiscordToken=\n", restoredConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-admin-secret", restoredConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-discord-secret", restoredConfig, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidUtf8FailsClosedInsteadOfCopyingUnknownCredentialBytes()
    {
        var invalid = new byte[] { (byte)'R', (byte)'C', (byte)'O', (byte)'N', (byte)'=', 0xFF };

        Assert.Throws<InvalidDataException>(() =>
            ProjectZomboidPortableServerConfiguration.Sanitize(invalid));
    }

    [Fact]
    public async Task OversizedConfigurationIsRejectedBeforePortableFileReadOrRewrite()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "oversized.ini");
        await using (var stream = new FileStream(
                         path,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {
            stream.SetLength((4L * 1024 * 1024) + 1);
        }

        var readException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProjectZomboidPortableServerConfiguration.ReadSanitizedFileAsync(
                path,
                CancellationToken.None));
        var rewriteException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProjectZomboidPortableServerConfiguration.SanitizeFileAsync(
                path,
                CancellationToken.None));

        Assert.Contains("portable-state safety limit", readException.Message, StringComparison.Ordinal);
        Assert.Contains("portable-state safety limit", rewriteException.Message, StringComparison.Ordinal);
        Assert.Equal((4L * 1024 * 1024) + 1, new FileInfo(path).Length);
    }

    private string CreateServerBundle(string serverName)
    {
        var userData = Path.Combine(_root, $"user-data-{Guid.NewGuid():N}");
        var world = Path.Combine(userData, "Saves", "Multiplayer", serverName);
        var server = Path.Combine(userData, "Server");
        Directory.CreateDirectory(world);
        Directory.CreateDirectory(server);
        File.WriteAllBytes(Path.Combine(world, "map_t.bin"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(server, serverName + ".ini"), "PublicName=Steward\n");
        return userData;
    }

    private static GameInstallation Installation(string userData)
        => new(
            "project-zomboid:test",
            Path.GetDirectoryName(userData) ?? userData,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectZomboidInstallationDiscovery.UserDataPathKey] = userData
            });

    private static EnvironmentManifest EmptyEnvironment()
        => new(
            1,
            "project-zomboid",
            "test-build",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static async Task WriteBytesAsync(
        ZipArchive archive,
        string name,
        byte[] bytes)
    {
        var entry = archive.CreateEntry(name);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes);
    }

    private static async Task WriteTextAsync(
        ZipArchive archive,
        string name,
        string text)
    {
        var entry = archive.CreateEntry(name);
        await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        await writer.WriteAsync(text);
    }

    public void Dispose()
    {
        foreach (var package in _packages)
        {
            try
            {
                if (File.Exists(package))
                {
                    File.Delete(package);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

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
