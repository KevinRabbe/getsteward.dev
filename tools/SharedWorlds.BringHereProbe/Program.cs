using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.BringHereProbe;

internal static class Program
{
    private const int UsageError = 2;
    private const int EvidenceSchemaVersion = 1;
    private const int AcceptanceBuildSchemaVersion = 2;
    private const string EvidenceDocumentType = "safe-world.bring-here-two-pc-evidence";
    private const string AcceptanceBuildDocumentType = "steward.e4-desktop-acceptance-build";
    private const string SourceBeforeRole = "source-before";
    private const string TargetAfterRole = "target-after";
    private const string SourceAfterRole = "source-after";
    private const string DesktopProcessName = "SharedWorlds.Desktop";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 48,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        MaxDepth = 48
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Any(IsHelpArgument))
        {
            PrintUsage();
            return 0;
        }

        if (args.Length == 1 &&
            string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await RunSelfTestAsync();
                Console.WriteLine("[OK] Bring Here acceptance probe self-test passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Bring Here acceptance probe self-test failed: {exception.Message}");
                return 1;
            }
        }

        if (args.Length == 0 || !TryNormalizeRole(args[0], out var role))
        {
            PrintUsage();
            return UsageError;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            RequireWindows();
            var options = ParseOptions(args[1..]);
            ValidateOptions(role, options);

            var worldId = ParseWorldId(options["--world-id"]);
            var buildManifestPath = RequireExistingFile(options["--build-manifest"], ".json");
            var outputPath = RequireOutputPath(options["--output"]);
            var safeWorldRoot = options.TryGetValue("--safe-world-root", out var explicitRoot)
                ? Path.GetFullPath(explicitRoot)
                : GetDefaultSafeWorldRoot();

            var desktopProcessCount = CountDesktopProcesses();
            if (desktopProcessCount != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one running Safe World desktop process while collecting physical evidence, but found {desktopProcessCount}.");
            }

            var build = await InspectAcceptanceBuildAsync(
                buildManifestPath,
                cancellation.Token);
            var snapshot = await InspectLocalSnapshotAsync(
                safeWorldRoot,
                worldId,
                cancellation.Token);

            BringHereAcceptanceEvidence evidence;
            switch (role)
            {
                case SourceBeforeRole:
                    evidence = CreateEvidence(
                        role,
                        desktopProcessCount,
                        build,
                        snapshot,
                        sourceEvidenceSha256: null,
                        targetEvidenceSha256: null);
                    break;

                case TargetAfterRole:
                {
                    var sourcePath = RequireExistingFile(options["--source-evidence"], ".json");
                    var source = await ReadEvidenceAsync(
                        sourcePath,
                        SourceBeforeRole,
                        cancellation.Token);
                    var sourceHash = await ComputeFileSha256Async(sourcePath, cancellation.Token);
                    evidence = CreateEvidence(
                        role,
                        desktopProcessCount,
                        build,
                        snapshot,
                        sourceHash,
                        targetEvidenceSha256: null);
                    RequireTargetMatchesSource(source, evidence);
                    break;
                }

                case SourceAfterRole:
                {
                    var sourcePath = RequireExistingFile(options["--source-evidence"], ".json");
                    var targetPath = RequireExistingFile(options["--target-evidence"], ".json");
                    var source = await ReadEvidenceAsync(
                        sourcePath,
                        SourceBeforeRole,
                        cancellation.Token);
                    var target = await ReadEvidenceAsync(
                        targetPath,
                        TargetAfterRole,
                        cancellation.Token);
                    var sourceHash = await ComputeFileSha256Async(sourcePath, cancellation.Token);
                    var targetHash = await ComputeFileSha256Async(targetPath, cancellation.Token);
                    evidence = CreateEvidence(
                        role,
                        desktopProcessCount,
                        build,
                        snapshot,
                        sourceHash,
                        targetHash);
                    RequireSourceStillExact(source, target, evidence, sourceHash);
                    break;
                }

                default:
                    throw new InvalidOperationException($"Unsupported evidence role '{role}'.");
            }

            await WriteEvidenceAsync(outputPath, evidence, cancellation.Token);
            PrintEvidenceSummary(evidence, outputPath);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Bring Here acceptance evidence collection was cancelled.");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Bring Here acceptance evidence failed: {exception.Message}");
            return 1;
        }
    }

    private static bool IsHelpArgument(string argument)
        => string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(argument, "-h", StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeRole(string argument, out string role)
    {
        if (string.Equals(argument, "--source-before", StringComparison.OrdinalIgnoreCase))
        {
            role = SourceBeforeRole;
            return true;
        }

        if (string.Equals(argument, "--target-after", StringComparison.OrdinalIgnoreCase))
        {
            role = TargetAfterRole;
            return true;
        }

        if (string.Equals(argument, "--source-after", StringComparison.OrdinalIgnoreCase))
        {
            role = SourceAfterRole;
            return true;
        }

        role = string.Empty;
        return false;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Safe World private Bring Here two-PC acceptance evidence probe");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  SharedWorlds.BringHereProbe.exe --source-before --world-id <id> --build-manifest <acceptance-build.json> --output <pc-a-before.json>");
        Console.WriteLine("  SharedWorlds.BringHereProbe.exe --target-after --world-id <id> --build-manifest <acceptance-build.json> --source-evidence <pc-a-before.json> --output <pc-b-after.json>");
        Console.WriteLine("  SharedWorlds.BringHereProbe.exe --source-after --world-id <id> --build-manifest <acceptance-build.json> --source-evidence <pc-a-before.json> --target-evidence <pc-b-after.json> --output <pc-a-after.json>");
        Console.WriteLine();
        Console.WriteLine("Optional for isolated testing: --safe-world-root <path>");
        Console.WriteLine("Probe validation: --self-test");
        Console.WriteLine();
        Console.WriteLine("The probe is read-only outside --self-test. It verifies exact local storage, protected publication-journal evidence, installation identity, package bytes, and linked evidence files.");
        Console.WriteLine("It does not inspect the live backend or declare the physical acceptance PASS by itself. Keep the required UI screenshots and observations described in START-HERE-BRING-HERE-TWO-PC.txt.");
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Physical Bring Here evidence must be collected on the Windows PCs used for the test.");
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] arguments)
    {
        if (arguments.Length == 0 || arguments.Length % 2 != 0)
        {
            throw new ArgumentException("Every probe option must be supplied as an option/value pair.");
        }

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var name = arguments[index];
            var value = arguments[index + 1];
            if (!name.StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Every probe option must be supplied as an option/value pair.");
            }

            if (!options.TryAdd(name, value.Trim()))
            {
                throw new ArgumentException($"Option '{name}' was supplied more than once.");
            }
        }

        return options;
    }

    private static void ValidateOptions(
        string role,
        IReadOnlyDictionary<string, string> options)
    {
        var required = role switch
        {
            SourceBeforeRole => new[]
            {
                "--world-id", "--build-manifest", "--output"
            },
            TargetAfterRole => new[]
            {
                "--world-id", "--build-manifest", "--source-evidence", "--output"
            },
            SourceAfterRole => new[]
            {
                "--world-id", "--build-manifest", "--source-evidence", "--target-evidence", "--output"
            },
            _ => throw new InvalidOperationException($"Unsupported evidence role '{role}'.")
        };
        var allowed = required
            .Append("--safe-world-root")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var option in options.Keys)
        {
            if (!allowed.Contains(option))
            {
                throw new ArgumentException($"Unknown option '{option}'.");
            }
        }

        foreach (var option in required)
        {
            if (!options.ContainsKey(option))
            {
                throw new ArgumentException($"Required option '{option}' was not supplied.");
            }
        }
    }

    private static WorldId ParseWorldId(string value)
    {
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            throw new ArgumentException("World ID must be a non-empty GUID.");
        }

        return new WorldId(parsed);
    }

    private static string RequireExistingFile(string value, string extension)
    {
        var path = Path.GetFullPath(value);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Required evidence input does not exist.", path);
        }

        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Input file '{path}' must use the '{extension}' extension.");
        }

        return path;
    }

    private static string RequireOutputPath(string value)
    {
        var path = Path.GetFullPath(value);
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Evidence output must use the '.json' extension.");
        }

        return path;
    }

    private static string GetDefaultSafeWorldRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("Windows did not provide a LocalApplicationData directory.");
        }

        return Path.Combine(localData, "SharedWorlds");
    }

    private static int CountDesktopProcesses()
    {
        var processes = Process.GetProcessesByName(DesktopProcessName);
        try
        {
            return processes.Length;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static async Task<BuildEvidence> InspectAcceptanceBuildAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        AcceptanceBuildManifest manifest;
        await using (var input = new FileStream(
                         manifestPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            manifest = await JsonSerializer.DeserializeAsync<AcceptanceBuildManifest>(
                           input,
                           JsonOptions,
                           cancellationToken)
                       ?? throw new InvalidDataException("Acceptance-build manifest is empty.");
        }

        if (!string.Equals(manifest.DocumentType, AcceptanceBuildDocumentType, StringComparison.Ordinal) ||
            manifest.SchemaVersion != AcceptanceBuildSchemaVersion)
        {
            throw new InvalidDataException("Unsupported Safe World acceptance-build manifest.");
        }

        if (!IsHex(manifest.CommitSha, 40))
        {
            throw new InvalidDataException("Acceptance-build manifest requires an exact 40-character commit SHA.");
        }

        if (manifest.Files is null || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("Acceptance-build manifest contains no package files.");
        }

        var packageRoot = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("Acceptance-build manifest has no package directory.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var verified = new List<VerifiedPackageFile>(manifest.Files.Count);
        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path) ||
                file.ByteSize < 0 ||
                !IsHex(file.Sha256, 64))
            {
                throw new InvalidDataException("Acceptance-build manifest contains an invalid file entry.");
            }

            var normalizedPath = file.Path.Replace('\\', '/');
            if (!seen.Add(normalizedPath))
            {
                throw new InvalidDataException(
                    $"Acceptance-build manifest contains duplicate path '{normalizedPath}'.");
            }

            var fullPath = ResolvePackagePath(packageRoot, normalizedPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    $"Acceptance package file '{normalizedPath}' is missing.",
                    fullPath);
            }

            var info = new FileInfo(fullPath);
            if (info.Length != file.ByteSize)
            {
                throw new InvalidDataException(
                    $"Acceptance package file '{normalizedPath}' has the wrong byte size.");
            }

            var actualHash = await ComputeFileSha256Async(fullPath, cancellationToken);
            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Acceptance package file '{normalizedPath}' has the wrong SHA-256.");
            }

            verified.Add(new VerifiedPackageFile(normalizedPath, info.Length, actualHash));
        }

        var desktop = verified.SingleOrDefault(file =>
            string.Equals(file.Path, manifest.Executable, StringComparison.OrdinalIgnoreCase));
        if (desktop is null)
        {
            throw new InvalidDataException("Acceptance manifest does not contain its declared desktop executable.");
        }

        if (!verified.Any(file =>
                string.Equals(
                    file.Path,
                    "acceptance-tools/SharedWorlds.BringHereProbe.exe",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Acceptance package does not contain the Bring Here evidence probe.");
        }

        return new BuildEvidence(
            manifest.CommitSha.ToLowerInvariant(),
            await ComputeFileSha256Async(manifestPath, cancellationToken),
            ComputePackageFingerprint(verified),
            desktop.Sha256);
    }

    private static string ResolvePackagePath(string root, string relativePath)
    {
        var platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(platformPath))
        {
            throw new InvalidDataException("Acceptance manifest contains a rooted package path.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, platformPath));
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) ||
            string.Equals(relative, "..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Acceptance manifest contains a path outside its package directory.");
        }

        return fullPath;
    }

    private static string ComputePackageFingerprint(IEnumerable<VerifiedPackageFile> files)
    {
        var canonical = new StringBuilder();
        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            canonical.Append(file.Path);
            canonical.Append('\t');
            canonical.Append(file.ByteSize.ToString(CultureInfo.InvariantCulture));
            canonical.Append('\t');
            canonical.Append(file.Sha256.ToLowerInvariant());
            canonical.Append('\n');
        }

        return ComputeSha256(Encoding.UTF8.GetBytes(canonical.ToString()));
    }

    private static async Task<LocalSnapshotEvidence> InspectLocalSnapshotAsync(
        string safeWorldRoot,
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(safeWorldRoot);
        var installationId = await ReadInstallationIdAsync(
            Path.Combine(root, "settings", "device.json"),
            cancellationToken);
        var storageRoot = Path.Combine(root, "data");
        var storage = new LocalWorldStorage(storageRoot);
        var world = await storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new InvalidDataException($"World '{worldId}' is not materialized in canonical local storage.");

        if (world.SharingMode != WorldSharingMode.LocalOnly)
        {
            throw new InvalidDataException(
                $"World '{worldId}' is not a private LocalOnly World and cannot qualify for private Bring Here evidence.");
        }

        var stateId = world.CurrentStateRevisionId
            ?? throw new InvalidDataException($"World '{worldId}' has no current state revision.");
        var environmentId = world.CurrentEnvironmentRevisionId
            ?? throw new InvalidDataException($"World '{worldId}' has no current environment revision.");
        var state = await storage.LoadStateRevisionAsync(worldId, stateId, cancellationToken)
            ?? throw new InvalidDataException($"World '{worldId}' is missing its current state metadata.");
        var environment = await storage.LoadEnvironmentRevisionAsync(
                              worldId,
                              environmentId,
                              cancellationToken)
                          ?? throw new InvalidDataException(
                              $"World '{worldId}' is missing its current environment metadata.");

        if (state.EnvironmentRevisionId != environment.Id ||
            !string.Equals(state.AdapterId, world.GameAdapterId, StringComparison.Ordinal) ||
            !string.Equals(environment.Manifest.AdapterId, world.GameAdapterId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"World '{worldId}' has contradictory canonical state/environment adapter evidence.");
        }

        if (!await storage.IsRevisionPayloadAvailableAsync(worldId, stateId, cancellationToken))
        {
            throw new InvalidDataException($"World '{worldId}' current state payload is not locally available.");
        }

        long payloadLength;
        string payloadHash;
        await using (var payload = await storage.OpenRevisionAsync(worldId, stateId, cancellationToken))
        {
            (payloadLength, payloadHash) = await ComputeStreamEvidenceAsync(payload, cancellationToken);
        }

        var journal = new LocalOwnedWorldLocationPublicationJournal(storageRoot);
        var publication = await journal.LoadAsync(worldId, cancellationToken)
            ?? throw new InvalidDataException(
                $"World '{worldId}' has no durable location-publication evidence on this installation.");
        publication.Validate();

        if (!string.Equals(publication.InstallationId, installationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"World '{worldId}' publication journal belongs to a different installation identity.");
        }

        if (!publication.IsSynchronized || publication.InFlight is not null)
        {
            throw new InvalidDataException(
                $"World '{worldId}' location publication is not fully synchronized yet. Keep Safe World online, refresh, and run the check again.");
        }

        if (publication.DesiredStateRevisionId != stateId ||
            publication.DesiredEnvironmentRevisionId != environmentId ||
            publication.ConfirmedStateRevisionId != stateId ||
            publication.ConfirmedEnvironmentRevisionId != environmentId)
        {
            throw new InvalidDataException(
                $"World '{worldId}' publication journal does not confirm its exact canonical head.");
        }

        if (publication.DesiredPresentation is not { } desiredPresentation ||
            publication.ConfirmedPresentation is not { } confirmedPresentation ||
            !string.Equals(desiredPresentation.Name, world.Name, StringComparison.Ordinal) ||
            !string.Equals(desiredPresentation.GameAdapterId, world.GameAdapterId, StringComparison.Ordinal) ||
            desiredPresentation != confirmedPresentation)
        {
            throw new InvalidDataException(
                $"World '{worldId}' publication journal does not confirm its current presentation.");
        }

        var worldEvidence = new WorldEvidence(
            world.Id.ToString(),
            world.Name,
            world.GameAdapterId,
            (int)world.SharingMode,
            state.Id.ToString(),
            environment.Id.ToString(),
            state.ParentRevisionId?.ToString(),
            environment.ParentRevisionId?.ToString(),
            state.StatePackageId,
            environment.Manifest.GameVersion,
            ComputeCanonicalStateRevisionHash(state),
            ComputeCanonicalEnvironmentRevisionHash(environment),
            payloadLength,
            payloadHash);
        var journalEvidence = new JournalEvidence(
            publication.InstallationId,
            publication.DesiredStateRevisionId?.ToString(),
            publication.DesiredEnvironmentRevisionId?.ToString(),
            publication.ConfirmedStateRevisionId?.ToString(),
            publication.ConfirmedEnvironmentRevisionId?.ToString(),
            desiredPresentation.Name,
            desiredPresentation.GameAdapterId,
            confirmedPresentation.Name,
            confirmedPresentation.GameAdapterId,
            publication.IsSynchronized);
        return new LocalSnapshotEvidence(installationId, worldEvidence, journalEvidence);
    }

    private static async Task<string> ReadInstallationIdAsync(
        string settingsPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(settingsPath))
        {
            throw new FileNotFoundException("Safe World durable device settings are missing.", settingsPath);
        }

        DeviceSettingsEnvelope settings;
        await using (var input = new FileStream(
                         settingsPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         4096,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            settings = await JsonSerializer.DeserializeAsync<DeviceSettingsEnvelope>(
                           input,
                           JsonOptions,
                           cancellationToken)
                       ?? throw new InvalidDataException("Safe World device settings are empty.");
        }

        if (!string.Equals(settings.DocumentType, "sharedworlds.device-settings", StringComparison.Ordinal) ||
            settings.SchemaVersion != 2 ||
            settings.Payload is null ||
            string.IsNullOrWhiteSpace(settings.Payload.InstallationId) ||
            settings.Payload.InstallationId.Length > 128 ||
            settings.Payload.InstallationId.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("Safe World device settings do not contain a valid durable installation ID.");
        }

        return settings.Payload.InstallationId;
    }

    private static BringHereAcceptanceEvidence CreateEvidence(
        string role,
        int desktopProcessCount,
        BuildEvidence build,
        LocalSnapshotEvidence snapshot,
        string? sourceEvidenceSha256,
        string? targetEvidenceSha256)
        => new(
            EvidenceDocumentType,
            EvidenceSchemaVersion,
            role,
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            desktopProcessCount,
            snapshot.InstallationId,
            build,
            snapshot.World,
            snapshot.Journal,
            sourceEvidenceSha256,
            targetEvidenceSha256);

    private static void RequireTargetMatchesSource(
        BringHereAcceptanceEvidence source,
        BringHereAcceptanceEvidence target)
    {
        if (source.Role != SourceBeforeRole || target.Role != TargetAfterRole)
        {
            throw new InvalidDataException("Target evidence must link to source-before evidence.");
        }

        RequireSameBuild(source.Build, target.Build);
        if (source.World != target.World)
        {
            throw new InvalidDataException(
                "Target canonical World/revision/package evidence does not exactly match the source.");
        }

        if (string.Equals(source.InstallationId, target.InstallationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Source and target use the same durable installation ID.");
        }

        if (string.Equals(source.MachineName, target.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Source and target evidence were collected under the same Windows machine name.");
        }

        RequireJournalMatchesWorld(target);
    }

    private static void RequireSourceStillExact(
        BringHereAcceptanceEvidence sourceBefore,
        BringHereAcceptanceEvidence targetAfter,
        BringHereAcceptanceEvidence sourceAfter,
        string sourceEvidenceSha256)
    {
        if (!string.Equals(
                targetAfter.SourceEvidenceSha256,
                sourceEvidenceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Target evidence is not linked to the supplied source-before evidence bytes.");
        }

        RequireTargetMatchesSource(sourceBefore, targetAfter);
        RequireSameBuild(sourceBefore.Build, sourceAfter.Build);
        if (sourceBefore.World != sourceAfter.World)
        {
            throw new InvalidDataException(
                "The source canonical World changed after Bring Here; source preservation is not proven.");
        }

        if (!string.Equals(
                sourceBefore.InstallationId,
                sourceAfter.InstallationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                sourceBefore.MachineName,
                sourceAfter.MachineName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Source-after evidence was not collected from the original source installation.");
        }

        if (string.Equals(
                sourceAfter.InstallationId,
                targetAfter.InstallationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Source and target installation identities are not distinct.");
        }

        RequireJournalMatchesWorld(sourceAfter);
    }

    private static void RequireSameBuild(BuildEvidence expected, BuildEvidence actual)
    {
        if (expected != actual)
        {
            throw new InvalidDataException(
                "Both PCs must use the exact same byte-verified Safe World acceptance package.");
        }
    }

    private static void RequireJournalMatchesWorld(BringHereAcceptanceEvidence evidence)
    {
        if (!evidence.Journal.IsSynchronized ||
            !string.Equals(evidence.Journal.InstallationId, evidence.InstallationId, StringComparison.Ordinal) ||
            !string.Equals(evidence.Journal.DesiredStateRevisionId, evidence.World.StateRevisionId, StringComparison.Ordinal) ||
            !string.Equals(evidence.Journal.DesiredEnvironmentRevisionId, evidence.World.EnvironmentRevisionId, StringComparison.Ordinal) ||
            !string.Equals(evidence.Journal.ConfirmedStateRevisionId, evidence.World.StateRevisionId, StringComparison.Ordinal) ||
            !string.Equals(evidence.Journal.ConfirmedEnvironmentRevisionId, evidence.World.EnvironmentRevisionId, StringComparison.Ordinal) ||
            !string.Equals(evidence.Journal.ConfirmedName, evidence.World.Name, StringComparison.Ordinal) ||
            !string.Equals(evidence.Journal.ConfirmedGameAdapterId, evidence.World.GameAdapterId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Publication-journal evidence does not confirm the canonical World head.");
        }
    }

    private static async Task<BringHereAcceptanceEvidence> ReadEvidenceAsync(
        string path,
        string expectedRole,
        CancellationToken cancellationToken)
    {
        BringHereAcceptanceEvidence evidence;
        await using (var input = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            evidence = await JsonSerializer.DeserializeAsync<BringHereAcceptanceEvidence>(
                           input,
                           JsonOptions,
                           cancellationToken)
                       ?? throw new InvalidDataException($"Evidence file '{path}' is empty.");
        }

        if (!string.Equals(evidence.DocumentType, EvidenceDocumentType, StringComparison.Ordinal) ||
            evidence.SchemaVersion != EvidenceSchemaVersion ||
            !string.Equals(evidence.Role, expectedRole, StringComparison.Ordinal) ||
            evidence.CollectedAtUtc == default ||
            string.IsNullOrWhiteSpace(evidence.MachineName) ||
            evidence.DesktopProcessCount != 1 ||
            string.IsNullOrWhiteSpace(evidence.InstallationId))
        {
            throw new InvalidDataException($"Evidence file '{path}' is not valid {expectedRole} evidence.");
        }

        RequireJournalMatchesWorld(evidence);
        return evidence;
    }

    private static async Task WriteEvidenceAsync(
        string path,
        BringHereAcceptanceEvidence evidence,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Evidence output path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    output,
                    evidence,
                    JsonOptions,
                    cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void PrintEvidenceSummary(
        BringHereAcceptanceEvidence evidence,
        string outputPath)
    {
        Console.WriteLine("Safe World private Bring Here two-PC acceptance evidence");
        Console.WriteLine();
        Console.WriteLine($"Role: {evidence.Role}");
        Console.WriteLine($"Machine: {evidence.MachineName}");
        Console.WriteLine($"Installation: {evidence.InstallationId}");
        Console.WriteLine($"Safe World commit: {evidence.Build.CommitSha}");
        Console.WriteLine($"Package fingerprint: {evidence.Build.PackageFingerprintSha256}");
        Console.WriteLine($"World ID: {evidence.World.WorldId}");
        Console.WriteLine($"State revision: {evidence.World.StateRevisionId}");
        Console.WriteLine($"Environment revision: {evidence.World.EnvironmentRevisionId}");
        Console.WriteLine($"Payload SHA-256: {evidence.World.PayloadSha256}");
        Console.WriteLine($"Evidence: {outputPath}");
        Console.WriteLine();
        Console.WriteLine("[OK] Local identity, immutable revision, payload, build, and publication-journal evidence are internally consistent.");
        Console.WriteLine("This does not by itself declare the physical two-PC acceptance PASS. Preserve the required screenshots and observations.");
    }

    private static string ComputeCanonicalStateRevisionHash(StateRevision revision)
    {
        var canonical = new StateRevisionFingerprint(
            revision.Id.ToString(),
            revision.WorldId.ToString(),
            revision.ParentRevisionId?.ToString(),
            revision.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ToIdentity(revision.CreatedBy),
            revision.AdapterId,
            revision.StatePackageId,
            revision.EnvironmentRevisionId?.ToString());
        return ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(canonical, CanonicalJsonOptions));
    }

    private static string ComputeCanonicalEnvironmentRevisionHash(EnvironmentRevision revision)
    {
        var components = revision.Manifest.Components
            .Select(component => new EnvironmentComponentFingerprint(
                component.Kind,
                component.Id,
                component.Version,
                component.Source,
                SortDictionary(component.Metadata)))
            .ToArray();
        var canonical = new EnvironmentRevisionFingerprint(
            revision.Id.ToString(),
            revision.WorldId.ToString(),
            revision.ParentRevisionId?.ToString(),
            revision.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ToIdentity(revision.CreatedBy),
            revision.Manifest.SchemaVersion,
            revision.Manifest.AdapterId,
            revision.Manifest.GameVersion,
            components,
            SortDictionary(revision.Manifest.Configuration));
        return ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(canonical, CanonicalJsonOptions));
    }

    private static IdentityFingerprint? ToIdentity(UserIdentity? identity)
        => identity is null
            ? null
            : new IdentityFingerprint(identity.Provider, identity.ExternalId, identity.DisplayName);

    private static KeyValueFingerprint[] SortDictionary(
        IReadOnlyDictionary<string, string>? values)
        => values is null
            ? []
            : values
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new KeyValueFingerprint(pair.Key, pair.Value))
                .ToArray();

    private static async Task<(long Length, string Sha256)> ComputeStreamEvidenceAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            length = checked(length + read);
        }

        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var (_, hash) = await ComputeStreamEvidenceAsync(input, cancellationToken);
        return hash;
    }

    private static string ComputeSha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsHex(string? value, int length)
        => value is { Length: var actualLength } &&
           actualLength == length &&
           value.All(Uri.IsHexDigit);

    private static async Task RunSelfTestAsync()
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
            await WriteSelfTestManifestAsync(packageRoot, manifestPath);
            var build = await InspectAcceptanceBuildAsync(manifestPath, CancellationToken.None);

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
                ParentRevisionId: null,
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
                ParentRevisionId: null,
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

            var sourceSnapshot = await InspectLocalSnapshotAsync(
                sourceRoot,
                worldId,
                CancellationToken.None);
            var targetSnapshot = await InspectLocalSnapshotAsync(
                targetRoot,
                worldId,
                CancellationToken.None);
            var sourceEvidence = CreateEvidence(
                SourceBeforeRole,
                1,
                build,
                sourceSnapshot,
                null,
                null) with { MachineName = "SOURCE-PC" };
            var targetEvidence = CreateEvidence(
                TargetAfterRole,
                1,
                build,
                targetSnapshot,
                new string('a', 64),
                null) with { MachineName = "TARGET-PC" };
            RequireTargetMatchesSource(sourceEvidence, targetEvidence);

            var sourceAfter = CreateEvidence(
                SourceAfterRole,
                1,
                build,
                sourceSnapshot,
                new string('a', 64),
                new string('b', 64)) with { MachineName = "SOURCE-PC" };
            RequireSourceStillExact(
                sourceEvidence,
                targetEvidence,
                sourceAfter,
                new string('a', 64));

            var invalidInstallation = targetEvidence with
            {
                InstallationId = sourceEvidence.InstallationId,
                Journal = targetEvidence.Journal with
                {
                    InstallationId = sourceEvidence.InstallationId
                }
            };
            ExpectFailure(
                () => RequireTargetMatchesSource(sourceEvidence, invalidInstallation),
                "same installation identity");

            var invalidPayload = targetEvidence with
            {
                World = targetEvidence.World with
                {
                    PayloadSha256 = new string('f', 64)
                }
            };
            ExpectFailure(
                () => RequireTargetMatchesSource(sourceEvidence, invalidPayload),
                "payload mismatch");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Test cleanup only.
            }
            catch (UnauthorizedAccessException)
            {
                // Test cleanup only.
            }
        }
    }

    private static async Task WriteSelfTestManifestAsync(
        string packageRoot,
        string manifestPath)
    {
        var files = new List<AcceptanceBuildFile>();
        foreach (var path in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(path, manifestPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(path);
            files.Add(new AcceptanceBuildFile(
                Path.GetRelativePath(packageRoot, path).Replace('\\', '/'),
                info.Length,
                await ComputeFileSha256Async(path, CancellationToken.None)));
        }

        var manifest = new AcceptanceBuildManifest(
            AcceptanceBuildDocumentType,
            AcceptanceBuildSchemaVersion,
            new string('1', 40),
            "SharedWorlds.Desktop.exe",
            files);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
            JsonSerializer.Serialize(settings, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var storageRoot = Path.Combine(safeWorldRoot, "data");
        var storage = new LocalWorldStorage(storageRoot);
        await storage.StoreEnvironmentRevisionAsync(environment);
        await using (var package = new MemoryStream(payload, writable: false))
        {
            await storage.StoreRevisionAsync(state, package);
        }

        await storage.SaveWorldAsync(world);
        var presentation = new OwnedWorldPresentation(world.Name, world.GameAdapterId);
        var publication = new OwnedWorldLocationPublicationState(
            world.Id,
            installationId,
            state.Id,
            environment.Id,
            state.Id,
            environment.Id,
            InFlight: null,
            DateTimeOffset.UtcNow,
            presentation,
            presentation);
        var journal = new LocalOwnedWorldLocationPublicationJournal(storageRoot);
        await journal.SaveAsync(publication);
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

    private sealed record AcceptanceBuildManifest(
        string DocumentType,
        int SchemaVersion,
        string CommitSha,
        string Executable,
        IReadOnlyList<AcceptanceBuildFile>? Files);

    private sealed record AcceptanceBuildFile(
        string Path,
        long ByteSize,
        string Sha256);

    private sealed record VerifiedPackageFile(
        string Path,
        long ByteSize,
        string Sha256);

    private sealed record DeviceSettingsEnvelope(
        string DocumentType,
        int SchemaVersion,
        DeviceSettingsPayload? Payload);

    private sealed record DeviceSettingsPayload(
        bool AllowHosting,
        bool HostingPreferenceExplicit,
        string? InstallationId);

    private sealed record LocalSnapshotEvidence(
        string InstallationId,
        WorldEvidence World,
        JournalEvidence Journal);

    private sealed record StateRevisionFingerprint(
        string Id,
        string WorldId,
        string? ParentRevisionId,
        string CreatedAtUtc,
        IdentityFingerprint? CreatedBy,
        string AdapterId,
        string StatePackageId,
        string? EnvironmentRevisionId);

    private sealed record EnvironmentRevisionFingerprint(
        string Id,
        string WorldId,
        string? ParentRevisionId,
        string CreatedAtUtc,
        IdentityFingerprint? CreatedBy,
        int ManifestSchemaVersion,
        string AdapterId,
        string GameVersion,
        IReadOnlyList<EnvironmentComponentFingerprint> Components,
        IReadOnlyList<KeyValueFingerprint> Configuration);

    private sealed record EnvironmentComponentFingerprint(
        string Kind,
        string Id,
        string? Version,
        string? Source,
        IReadOnlyList<KeyValueFingerprint> Metadata);

    private sealed record IdentityFingerprint(
        string Provider,
        string ExternalId,
        string? DisplayName);

    private sealed record KeyValueFingerprint(string Key, string Value);

    private sealed record BuildEvidence(
        string CommitSha,
        string ManifestSha256,
        string PackageFingerprintSha256,
        string DesktopExecutableSha256);

    private sealed record WorldEvidence(
        string WorldId,
        string Name,
        string GameAdapterId,
        int SharingMode,
        string StateRevisionId,
        string EnvironmentRevisionId,
        string? StateParentRevisionId,
        string? EnvironmentParentRevisionId,
        string StatePackageId,
        string GameVersion,
        string StateRevisionSha256,
        string EnvironmentRevisionSha256,
        long PayloadByteSize,
        string PayloadSha256);

    private sealed record JournalEvidence(
        string InstallationId,
        string? DesiredStateRevisionId,
        string? DesiredEnvironmentRevisionId,
        string? ConfirmedStateRevisionId,
        string? ConfirmedEnvironmentRevisionId,
        string DesiredName,
        string DesiredGameAdapterId,
        string ConfirmedName,
        string ConfirmedGameAdapterId,
        bool IsSynchronized);

    private sealed record BringHereAcceptanceEvidence(
        string DocumentType,
        int SchemaVersion,
        string Role,
        DateTimeOffset CollectedAtUtc,
        string MachineName,
        int DesktopProcessCount,
        string InstallationId,
        BuildEvidence Build,
        WorldEvidence World,
        JournalEvidence Journal,
        string? SourceEvidenceSha256,
        string? TargetEvidenceSha256);
}
