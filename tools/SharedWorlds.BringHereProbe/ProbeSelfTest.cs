using System.Globalization;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.BringHereProbe;

internal static class ProbeSelfTest
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"safe-world-bring-here-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var packageRoot = Path.Combine(root, "package");
            Directory.CreateDirectory(Path.Combine(packageRoot, "acceptance-tools"));
            await File.WriteAllBytesAsync(
                Path.Combine(packageRoot, "SharedWorlds.Desktop.exe"),
                "desktop"u8.ToArray());
            await File.WriteAllBytesAsync(
                Path.Combine(packageRoot, "acceptance-tools", "SharedWorlds.BringHereProbe.exe"),
                "probe"u8.ToArray());
            var manifestPath = Path.Combine(packageRoot, "acceptance-build.json");
            await WriteManifestAsync(packageRoot, manifestPath);
            var build = await ProbeEngine.InspectAcceptanceBuildAsync(
                manifestPath,
                CancellationToken.None);

            var worldId = WorldId.New();
            var stateId = RevisionId.New();
            var environmentId = RevisionId.New();
            var createdAt = DateTimeOffset.Parse(
                "2026-08-04T00:00:00Z",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal);
            var sourceUser = new UserIdentity("steam", "source", "Source");
            var targetUser = new UserIdentity("steam", "target", "Target");
            var environment = new EnvironmentRevision(
                environmentId,
                worldId,
                null,
                createdAt,
                sourceUser,
                new EnvironmentManifest(
                    1,
                    "factorio",
                    "2.1.11",
                    [],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["mode"] = "vanilla"
                    }));
            var state = new StateRevision(
                stateId,
                worldId,
                null,
                createdAt,
                sourceUser,
                "factorio",
                "state-package",
                environmentId);
            var sourceWorld = new World(
                worldId,
                "Acceptance World",
                "factorio",
                [sourceUser],
                environmentId,
                stateId);
            var targetWorld = sourceWorld with { Members = [targetUser] };
            var payload = "exact-private-world-payload"u8.ToArray();

            var sourceRoot = Path.Combine(root, "source");
            var targetRoot = Path.Combine(root, "target");
            const string sourceInstallation = "11111111111111111111111111111111";
            const string targetInstallation = "22222222222222222222222222222222";
            await WriteFixtureAsync(
                sourceRoot,
                sourceInstallation,
                sourceWorld,
                environment,
                state,
                payload);
            await WriteFixtureAsync(
                targetRoot,
                targetInstallation,
                targetWorld,
                environment,
                state,
                payload);

            var sourceSnapshot = await ProbeEngine.InspectLocalSnapshotAsync(
                sourceRoot,
                worldId,
                CancellationToken.None);
            var targetSnapshot = await ProbeEngine.InspectLocalSnapshotAsync(
                targetRoot,
                worldId,
                CancellationToken.None);
            var source = ProbeEngine.CreateEvidence(
                ProbeContract.SourceBeforeRole,
                1,
                build,
                sourceSnapshot,
                "SOURCE-PC",
                null,
                null);
            var sourceHash = new string('a', 64);
            var target = ProbeEngine.CreateEvidence(
                ProbeContract.TargetAfterRole,
                1,
                build,
                targetSnapshot,
                "TARGET-PC",
                sourceHash,
                null);
            ProbeEngine.RequireTargetMatchesSource(source, target);

            var targetHash = new string('b', 64);
            var sourceAfter = ProbeEngine.CreateEvidence(
                ProbeContract.SourceAfterRole,
                1,
                build,
                sourceSnapshot,
                "SOURCE-PC",
                sourceHash,
                targetHash);
            ProbeEngine.RequireSourceStillExact(
                source,
                target,
                sourceAfter,
                sourceHash,
                targetHash);

            ExpectFailure(
                () => ProbeEngine.RequireTargetMatchesSource(
                    source,
                    target with
                    {
                        InstallationId = source.InstallationId,
                        Journal = target.Journal with
                        {
                            InstallationId = source.InstallationId
                        }
                    }),
                "same installation identity");
            ExpectFailure(
                () => ProbeEngine.RequireTargetMatchesSource(
                    source,
                    target with
                    {
                        World = target.World with
                        {
                            PayloadSha256 = new string('f', 64)
                        }
                    }),
                "payload mismatch");
            ExpectFailure(
                () => ProbeEngine.RequireSourceStillExact(
                    source,
                    target,
                    sourceAfter,
                    sourceHash,
                    new string('c', 64)),
                "unlinked target evidence");

            Console.WriteLine("[OK] Bring Here acceptance probe self-test passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Bring Here acceptance probe self-test failed: {exception.Message}");
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Isolated test cleanup only.
            }
            catch (UnauthorizedAccessException)
            {
                // Isolated test cleanup only.
            }
        }
    }

    private static async Task WriteFixtureAsync(
        string safeWorldRoot,
        string installationId,
        World world,
        EnvironmentRevision environment,
        StateRevision state,
        byte[] payload)
    {
        var settingsDirectory = Path.Combine(safeWorldRoot, "settings");
        Directory.CreateDirectory(settingsDirectory);
        var settings = new DeviceSettingsEnvelope(
            "sharedworlds.device-settings",
            2,
            new DeviceSettingsPayload(false, false, installationId));
        await File.WriteAllTextAsync(
            Path.Combine(settingsDirectory, "device.json"),
            JsonSerializer.Serialize(settings, ProbeContract.JsonOptions),
            new UTF8Encoding(false));

        var storageRoot = Path.Combine(safeWorldRoot, "data");
        var storage = new LocalWorldStorage(storageRoot);
        await storage.StoreEnvironmentRevisionAsync(environment);
        await using (var package = new MemoryStream(payload, writable: false))
        {
            await storage.StoreRevisionAsync(state, package);
        }

        await storage.SaveWorldAsync(world);
        var presentation = new OwnedWorldPresentation(world.Name, world.GameAdapterId);
        await new LocalOwnedWorldLocationPublicationJournal(storageRoot).SaveAsync(
            new OwnedWorldLocationPublicationState(
                world.Id,
                installationId,
                state.Id,
                environment.Id,
                state.Id,
                environment.Id,
                null,
                DateTimeOffset.UtcNow,
                presentation,
                presentation));
    }

    private static async Task WriteManifestAsync(string packageRoot, string manifestPath)
    {
        var files = new List<AcceptanceBuildFile>();
        foreach (var path in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            files.Add(new AcceptanceBuildFile(
                Path.GetRelativePath(packageRoot, path).Replace('\\', '/'),
                info.Length,
                await ProbeEngine.ComputeFileSha256Async(path, CancellationToken.None)));
        }

        var manifest = new AcceptanceBuildManifest(
            ProbeContract.AcceptanceBuildDocumentType,
            ProbeContract.AcceptanceBuildSchemaVersion,
            new string('1', 40),
            "2026-08-04T00:00:00Z",
            "win-x64",
            "Release",
            false,
            "SharedWorlds.Desktop.exe",
            "steam_api64.dll",
            files);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, ProbeContract.JsonOptions),
            new UTF8Encoding(false));
    }

    private static void ExpectFailure(Action action, string name)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException($"Self-test did not reject {name}.");
    }
}
