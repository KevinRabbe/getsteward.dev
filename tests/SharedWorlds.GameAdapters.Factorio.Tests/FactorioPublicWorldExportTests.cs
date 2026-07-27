using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.GameAdapters.Factorio;
using Xunit;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioPublicWorldExportTests
{
    [Fact]
    public async Task CurrentFactorioEnvironmentIsQualifiedForPublicExport()
    {
        var adapter = new FactorioAdapter();
        var environment = CreateEnvironment(schemaVersion: 1, adapterId: "factorio");

        var readiness = await ((IPublicWorldExportAdapter)adapter)
            .CheckPublicWorldExportAsync(environment);

        Assert.True(readiness.IsSupported);
        Assert.Null(readiness.Reason);
    }

    [Fact]
    public async Task FutureEnvironmentSchemaFailsClosed()
    {
        var adapter = new FactorioAdapter();
        var environment = CreateEnvironment(schemaVersion: 2, adapterId: "factorio");

        var readiness = await ((IPublicWorldExportAdapter)adapter)
            .CheckPublicWorldExportAsync(environment);

        Assert.False(readiness.IsSupported);
        Assert.Contains("has not been qualified", readiness.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongAdapterEnvironmentFailsClosed()
    {
        var adapter = new FactorioAdapter();
        var environment = CreateEnvironment(schemaVersion: 1, adapterId: "palworld");

        var readiness = await ((IPublicWorldExportAdapter)adapter)
            .CheckPublicWorldExportAsync(environment);

        Assert.False(readiness.IsSupported);
        Assert.Contains("not Factorio", readiness.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectedWorldCaptureCopiesOnlyNativeSaveZip()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "sharedworlds-factorio-public-export-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var savePath = Path.Combine(root, "500h-megabase.zip");
        var saveBytes = Encoding.UTF8.GetBytes("factorio-save-marker");
        await File.WriteAllBytesAsync(savePath, saveBytes);

        var fakeSecret = "do-not-publish-this-token";
        await File.WriteAllTextAsync(
            Path.Combine(root, "player-data.json"),
            $"{{\"token\":\"{fakeSecret}\"}}");
        await File.WriteAllTextAsync(
            Path.Combine(root, "config.ini"),
            "[path]\nwrite-data=private-device-path");

        CapturedState? captured = null;
        try
        {
            var adapter = new FactorioAdapter();
            captured = await adapter.CaptureDetectedWorldAsync(
                new GameInstallation(
                    Id: "test-installation",
                    RootPath: root,
                    Source: "test"),
                new DetectedWorld(
                    Id: savePath,
                    DisplayName: "500h Megabase",
                    SourcePath: savePath));

            var capturedBytes = await File.ReadAllBytesAsync(captured.Package.Path);
            Assert.Equal(saveBytes, capturedBytes);
            Assert.DoesNotContain(
                fakeSecret,
                Encoding.UTF8.GetString(capturedBytes),
                StringComparison.Ordinal);
        }
        finally
        {
            if (captured is not null && File.Exists(captured.Package.Path))
            {
                File.Delete(captured.Package.Path);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static EnvironmentManifest CreateEnvironment(int schemaVersion, string adapterId)
        => new(
            SchemaVersion: schemaVersion,
            AdapterId: adapterId,
            GameVersion: "2.0.72",
            Components:
            [
                new EnvironmentComponent(
                    Kind: "mod",
                    Id: "base",
                    Version: "2.0.72",
                    Source: "builtin"),
                new EnvironmentComponent(
                    Kind: "mod",
                    Id: "example-user-mod",
                    Version: "1.2.3",
                    Source: "user")
            ],
            Configuration: new Dictionary<string, string>
            {
                ["factorio.mod-settings.sha256"] = new string('A', 64)
            });
}
