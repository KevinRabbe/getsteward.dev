using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.BringHereProbe;

internal static class ProbeEngine
{
    public static string GetDefaultSafeWorldRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("Windows did not provide a LocalApplicationData directory.");
        }

        return Path.Combine(localData, "SharedWorlds");
    }

    public static int CountDesktopProcesses()
    {
        var processes = Process.GetProcessesByName(ProbeContract.DesktopProcessName);
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

    public static async Task<BuildEvidence> InspectAcceptanceBuildAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        AcceptanceBuildManifest manifest;
        await using (var input = OpenRead(manifestPath))
        {
            manifest = await JsonSerializer.DeserializeAsync<AcceptanceBuildManifest>(
                           input,
                           ProbeContract.JsonOptions,
                           cancellationToken)
                       ?? throw new InvalidDataException("Acceptance-build manifest is empty.");
        }

        if (!string.Equals(
                manifest.DocumentType,
                ProbeContract.AcceptanceBuildDocumentType,
                StringComparison.Ordinal) ||
            manifest.SchemaVersion != ProbeContract.AcceptanceBuildSchemaVersion)
        {
            throw new InvalidDataException("Unsupported Safe World acceptance-build manifest.");
        }

        if (!IsHex(manifest.CommitSha, 40))
        {
            throw new InvalidDataException("Acceptance-build manifest requires an exact 40-character commit SHA.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Executable) ||
            manifest.Files is null ||
            manifest.Files.Count == 0)
        {
            throw new InvalidDataException("Acceptance-build manifest is incomplete.");
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

    public static async Task<LocalSnapshotEvidence> InspectLocalSnapshotAsync(
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
                $"World '{worldId}' is not a private LocalOnly World.");
        }

        var stateId = world.CurrentStateRevisionId
            ?? throw new InvalidDataException($"World '{worldId}' has no current state revision.");
        var environmentId = world.CurrentEnvironmentRevisionId
            ?? throw new InvalidDataException($"World '{worldId}' has no current environment revision.");
        var state = await storage.LoadStateRevisionAsync(worldId, stateId, cancellationToken)
            ?? throw new InvalidDataException($"World '{worldId}' is missing current state metadata.");
        var environment = await storage.LoadEnvironmentRevisionAsync(
                              worldId,
                              environmentId,
                              cancellationToken)
                          ?? throw new InvalidDataException(
                              $"World '{worldId}' is missing current environment metadata.");

        if (state.EnvironmentRevisionId != environment.Id ||
            !string.Equals(state.AdapterId, world.GameAdapterId, StringComparison.Ordinal) ||
            !string.Equals(environment.Manifest.AdapterId, world.GameAdapterId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"World '{worldId}' has contradictory canonical state/environment evidence.");
        }

        if (!await storage.IsRevisionPayloadAvailableAsync(worldId, stateId, cancellationToken))
        {
            throw new InvalidDataException($"World '{worldId}' current state payload is absent.");
        }

        long payloadLength;
        string payloadHash;
        await using (var payload = await storage.OpenRevisionAsync(worldId, stateId, cancellationToken))
        {
            (payloadLength, payloadHash) = await ComputeStreamEvidenceAsync(payload, cancellationToken);
        }

        var publication = await new LocalOwnedWorldLocationPublicationJournal(storageRoot)
            .LoadAsync(worldId, cancellationToken)
            ?? throw new InvalidDataException(
                $"World '{worldId}' has no durable location-publication evidence.");
        publication.Validate();
        if (!string.Equals(publication.InstallationId, installationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"World '{worldId}' publication journal belongs to a different installation.");
        }

        if (!publication.IsSynchronized || publication.InFlight is not null)
        {
            throw new InvalidDataException(
                $"World '{worldId}' location publication is not synchronized yet. Keep Safe World online, refresh, and retry.");
        }

        if (publication.DesiredStateRevisionId != stateId ||
            publication.DesiredEnvironmentRevisionId != environmentId ||
            publication.ConfirmedStateRevisionId != stateId ||
            publication.ConfirmedEnvironmentRevisionId != environmentId)
        {
            throw new InvalidDataException(
                $"World '{worldId}' publication journal does not confirm its exact canonical head.");
        }

        var desired = publication.DesiredPresentation
            ?? throw new InvalidDataException("Publication journal has no desired World presentation.");
        var confirmed = publication.ConfirmedPresentation
            ?? throw new InvalidDataException("Publication journal has no confirmed World presentation.");
        if (!string.Equals(desired.Name, world.Name, StringComparison.Ordinal) ||
            !string.Equals(desired.GameAdapterId, world.GameAdapterId, StringComparison.Ordinal) ||
            desired != confirmed)
        {
            throw new InvalidDataException(
                $"World '{worldId}' publication journal does not confirm current presentation.");
        }

        return new LocalSnapshotEvidence(
            installationId,
            new WorldEvidence(
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
                payloadHash),
            new JournalEvidence(
                publication.InstallationId,
                publication.DesiredStateRevisionId?.ToString(),
                publication.DesiredEnvironmentRevisionId?.ToString(),
                publication.ConfirmedStateRevisionId?.ToString(),
                publication.ConfirmedEnvironmentRevisionId?.ToString(),
                desired.Name,
                desired.GameAdapterId,
                confirmed.Name,
                confirmed.GameAdapterId,
                publication.IsSynchronized));
    }

    public static BringHereAcceptanceEvidence CreateEvidence(
        string role,
        int desktopProcessCount,
        BuildEvidence build,
        LocalSnapshotEvidence snapshot,
        string machineName,
        string? sourceEvidenceSha256,
        string? targetEvidenceSha256)
        => new(
            ProbeContract.EvidenceDocumentType,
            ProbeContract.EvidenceSchemaVersion,
            role,
            DateTimeOffset.UtcNow,
            machineName,
            desktopProcessCount,
            snapshot.InstallationId,
            build,
            snapshot.World,
            snapshot.Journal,
            sourceEvidenceSha256,
            targetEvidenceSha256);

    public static void RequireTargetMatchesSource(
        BringHereAcceptanceEvidence source,
        BringHereAcceptanceEvidence target)
    {
        if (!string.Equals(source.Role, ProbeContract.SourceBeforeRole, StringComparison.Ordinal) ||
            !string.Equals(target.Role, ProbeContract.TargetAfterRole, StringComparison.Ordinal))
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
            throw new InvalidDataException("Source and target evidence use the same Windows machine name.");
        }

        RequireJournalMatchesWorld(target);
    }

    public static void RequireSourceStillExact(
        BringHereAcceptanceEvidence sourceBefore,
        BringHereAcceptanceEvidence targetAfter,
        BringHereAcceptanceEvidence sourceAfter,
        string sourceEvidenceSha256,
        string targetEvidenceSha256)
    {
        if (!string.Equals(
                targetAfter.SourceEvidenceSha256,
                sourceEvidenceSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                sourceAfter.SourceEvidenceSha256,
                sourceEvidenceSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                sourceAfter.TargetEvidenceSha256,
                targetEvidenceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The three evidence files are not cryptographically linked.");
        }

        RequireTargetMatchesSource(sourceBefore, targetAfter);
        RequireSameBuild(sourceBefore.Build, sourceAfter.Build);
        if (sourceBefore.World != sourceAfter.World)
        {
            throw new InvalidDataException(
                "The source canonical World changed after Bring Here; source preservation is not proven.");
        }

        if (!string.Equals(sourceBefore.InstallationId, sourceAfter.InstallationId, StringComparison.Ordinal) ||
            !string.Equals(sourceBefore.MachineName, sourceAfter.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Source-after evidence is not from the original source installation.");
        }

        if (string.Equals(sourceAfter.InstallationId, targetAfter.InstallationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Source and target installation identities are not distinct.");
        }

        RequireJournalMatchesWorld(sourceAfter);
    }

    public static async Task<BringHereAcceptanceEvidence> ReadEvidenceAsync(
        string path,
        string expectedRole,
        bool requirePhysicalProcess,
        CancellationToken cancellationToken)
    {
        BringHereAcceptanceEvidence evidence;
        await using (var input = OpenRead(path))
        {
            evidence = await JsonSerializer.DeserializeAsync<BringHereAcceptanceEvidence>(
                           input,
                           ProbeContract.JsonOptions,
                           cancellationToken)
                       ?? throw new InvalidDataException($"Evidence file '{path}' is empty.");
        }

        if (!string.Equals(evidence.DocumentType, ProbeContract.EvidenceDocumentType, StringComparison.Ordinal) ||
            evidence.SchemaVersion != ProbeContract.EvidenceSchemaVersion ||
            !string.Equals(evidence.Role, expectedRole, StringComparison.Ordinal) ||
            evidence.CollectedAtUtc == default ||
            string.IsNullOrWhiteSpace(evidence.MachineName) ||
            string.IsNullOrWhiteSpace(evidence.InstallationId) ||
            (requirePhysicalProcess && evidence.DesktopProcessCount != 1))
        {
            throw new InvalidDataException($"Evidence file '{path}' is not valid {expectedRole} evidence.");
        }

        RequireJournalMatchesWorld(evidence);
        return evidence;
    }

    public static async Task WriteEvidenceAsync(
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
                    ProbeContract.JsonOptions,
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

    public static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var input = OpenRead(path);
        var (_, hash) = await ComputeStreamEvidenceAsync(input, cancellationToken);
        return hash;
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
            !string.Equals(evidence.Journal.DesiredName, evidence.World.Name, StringComparison.Ordinal) ||
            !string.Equals(evidence.Journal.DesiredGameAdapterId, evidence.World.GameAdapterId, StringComparison.Ordinal) ||
            !string.Equals(evidence.Journal.ConfirmedName, evidence.World.Name, StringComparison.Ordinal) ||
            !string.Equals(evidence.Journal.ConfirmedGameAdapterId, evidence.World.GameAdapterId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Publication-journal evidence does not confirm the canonical World head.");
        }
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
        await using (var input = OpenRead(settingsPath))
        {
            settings = await JsonSerializer.DeserializeAsync<DeviceSettingsEnvelope>(
                           input,
                           ProbeContract.JsonOptions,
                           cancellationToken)
                       ?? throw new InvalidDataException("Safe World device settings are empty.");
        }

        var installationId = settings.Payload?.InstallationId;
        if (!string.Equals(settings.DocumentType, "sharedworlds.device-settings", StringComparison.Ordinal) ||
            settings.SchemaVersion != 2 ||
            string.IsNullOrWhiteSpace(installationId) ||
            installationId.Length > 128 ||
            installationId.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("Safe World device settings contain no valid durable installation ID.");
        }

        return installationId;
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
        return ComputeSha256(
            JsonSerializer.SerializeToUtf8Bytes(canonical, ProbeContract.CanonicalJsonOptions));
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
        return ComputeSha256(
            JsonSerializer.SerializeToUtf8Bytes(canonical, ProbeContract.CanonicalJsonOptions));
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

    private static string ComputeSha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsHex(string? value, int length)
        => value is { Length: var actualLength } &&
           actualLength == length &&
           value.All(Uri.IsHexDigit);

    private static FileStream OpenRead(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}
