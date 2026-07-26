using SharedWorlds.Desktop;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class DeviceSettingsMigrationAcceptanceTests
{
    [Fact]
    public async Task LegacySettingsMigrateAfterSourceReadHandleIsClosed()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "device.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "documentType": "sharedworlds.device-settings",
              "schemaVersion": 1,
              "payload": {
                "allowHosting": true,
                "hostingPreferenceExplicit": true
              }
            }
            """);

        var store = new DeviceSettingsStore(path);
        var migrated = await store.LoadOrCreateAsync(hasManagedWorlds: false);

        Assert.True(migrated.AllowHosting);
        Assert.True(migrated.HostingPreferenceExplicit);
        Assert.False(string.IsNullOrWhiteSpace(migrated.InstallationId));

        var reloaded = await new DeviceSettingsStore(path).LoadOrCreateAsync(hasManagedWorlds: false);
        Assert.Equal(migrated, reloaded);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "steward-device-settings-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
