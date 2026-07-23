namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldRestAcceptanceConfigurationTests
{
    [Fact]
    public void MissingConfigurationFailsClosed()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var result = PalworldRestAcceptanceConfigurationReader.Read(root);

            Assert.False(result.IsUsable);
            Assert.False(result.ConfigExists);
            Assert.False(result.AdminPasswordConfigured);
            Assert.Contains("not found", result.BlockingReasons.Single(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidConfigurationReportsPasswordPresenceAndSelectedWorld()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configPath = CreateConfig(root, "RESTAPIEnabled=True,RESTAPIPort=8212,AdminPassword=\"do-not-return-this\"");
            CreateSelectedWorld(root, "ABC123");

            var result = PalworldRestAcceptanceConfigurationReader.Read(root);

            Assert.Equal(configPath, result.ConfigPath);
            Assert.True(result.IsUsable);
            Assert.Equal(8212, result.RestPort);
            Assert.True(result.AdminPasswordConfigured);
            Assert.Equal("ABC123", result.SelectedWorldId);
            Assert.Equal("do-not-return-this", PalworldRestAcceptanceConfigurationReader.ReadAdminPassword(root));
            Assert.DoesNotContain("do-not-return-this", string.Join('|', result.BlockingReasons));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SelectedWorldComesFromGameUserSettingsNotPalWorldSettings()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            CreateConfig(
                root,
                "RESTAPIEnabled=True,RESTAPIPort=8212,AdminPassword=\"configured\",DedicatedServerName=WRONG-WORLD");
            CreateSelectedWorld(root, "RIGHT-WORLD");

            var result = PalworldRestAcceptanceConfigurationReader.Read(root);

            Assert.Equal("RIGHT-WORLD", result.SelectedWorldId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingGameUserSettingsLeavesSelectedWorldUnknownWithoutBreakingRestConfiguration()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            CreateConfig(root, "RESTAPIEnabled=True,RESTAPIPort=8212,AdminPassword=\"configured\"");

            var result = PalworldRestAcceptanceConfigurationReader.Read(root);

            Assert.True(result.IsUsable);
            Assert.Null(result.SelectedWorldId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DisabledOrInvalidConfigurationProducesBoundedReasons()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            CreateConfig(root, "RESTAPIEnabled=False,RESTAPIPort=99999,AdminPassword=");
            CreateSelectedWorld(root, "ABC123");

            var result = PalworldRestAcceptanceConfigurationReader.Read(root);

            Assert.False(result.IsUsable);
            Assert.False(result.RestEnabled);
            Assert.Null(result.RestPort);
            Assert.False(result.AdminPasswordConfigured);
            Assert.Equal(3, result.BlockingReasons.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "steward-palworld-rest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateConfig(string root, string values)
    {
        var directory = Path.Combine(root, "Pal", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "PalWorldSettings.ini");
        File.WriteAllText(path, $"OptionSettings=({values})");
        return path;
    }

    private static string CreateSelectedWorld(string root, string worldId)
    {
        var directory = Path.Combine(root, "Pal", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "GameUserSettings.ini");
        File.WriteAllText(path, $"[/Script/Engine.GameUserSettings]\nDedicatedServerName={worldId}\n");
        return path;
    }
}
