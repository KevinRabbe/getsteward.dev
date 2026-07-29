using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.GameAdapters.Factorio;
using SharedWorlds.Infrastructure.Portability;
using SharedWorlds.Infrastructure.Storage;

const int UsageError = 2;
const int EvidenceSchemaVersion = 1;
const string CreatorRole = "creator";
const string ViewerRole = "viewer";
const string AcceptanceBuildDocumentType = "steward.e4-desktop-acceptance-build";
const int AcceptanceBuildSchemaVersion = 2;
const string DesktopProcessName = "SharedWorlds.Desktop";

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    MaxDepth = 32,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
};

if (args.Any(argument =>
        string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(argument, "-h", StringComparison.OrdinalIgnoreCase)))
{
    PrintUsage();
    return;
}

if (args.Length == 0 ||
    (!string.Equals(args[0], "--creator", StringComparison.OrdinalIgnoreCase) &&
     !string.Equals(args[0], "--viewer", StringComparison.OrdinalIgnoreCase)))
{
    PrintUsage();
    Environment.ExitCode = UsageError;
    return;
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

    var role = string.Equals(args[0], "--creator", StringComparison.OrdinalIgnoreCase)
        ? CreatorRole
        : ViewerRole;
    var options = ParseOptions(args[1..]);
    RequireOnlyOptions(
        options,
        role == CreatorRole
            ? ["--file", "--build-manifest", "--output"]
            : ["--file", "--build-manifest", "--creator-evidence", "--output"]);

    var artifactPath = RequireExistingFile(options, "--file", ".safeworld");
    var buildManifestPath = RequireExistingFile(options, "--build-manifest");
    var outputPath = RequireOutputPath(options, "--output");

    var build = await InspectAcceptanceBuildAsync(buildManifestPath, jsonOptions, cancellation.Token);
    var artifact = await InspectPortableWorldAsync(artifactPath, cancellation.Token);
    var adapter = new FactorioAdapter();
    var exactFactorioInstallationCount = await RequireExactFactorioEnvironmentAsync(
        adapter,
        artifact.Preflight.Environment,
        cancellation.Token);
    RequireVanillaFactorio(artifact.Preflight.Environment);

    PortableWorldAcceptanceEvidence evidence;
    if (role == CreatorRole)
    {
        var creatorWorld = await FindCreatorWorldAsync(artifact.Preflight.SnapshotId, cancellation.Token);
        evidence = CreateCreatorEvidence(
            build,
            artifact,
            exactFactorioInstallationCount,
            creatorWorld);
    }
    else
    {
        var creatorEvidencePath = RequireExistingFile(options, "--creator-evidence", ".json");
        var creatorEvidence = await ReadCreatorEvidenceAsync(
            creatorEvidencePath,
            jsonOptions,
            cancellation.Token);
        RequireSameCreatorInputs(creatorEvidence, build, artifact);

        var viewerWorld = await FindViewerWorldAsync(artifact.Preflight.SnapshotId, cancellation.Token);
        RequireIndependentViewerWorld(viewerWorld, creatorEvidence, artifact.Preflight);

        var desktopProcessCount = CountDesktopProcesses();
        if (desktopProcessCount != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one running Safe World desktop process after file activation, but found {desktopProcessCount}.");
        }

        evidence = CreateViewerEvidence(
            build,
            artifact,
            exactFactorioInstallationCount,
            creatorEvidence,
            viewerWorld,
            desktopProcessCount);
    }

    await WriteEvidenceAsync(outputPath, evidence, jsonOptions, cancellation.Token);

    Console.WriteLine("Safe World portable World two-PC acceptance evidence");
    Console.WriteLine();
    Console.WriteLine($"Role: {evidence.Role}");
    Console.WriteLine($"Safe World commit: {evidence.SafeWorldCommitSha}");
    Console.WriteLine($"Package fingerprint: {evidence.PackageFingerprintSha256}");
    Console.WriteLine($"Factorio version: {evidence.RequiredFactorioVersion}");
    Console.WriteLine($"Portable World SHA-256: {evidence.ArtifactSha256}");
    Console.WriteLine($"Snapshot ID: {evidence.SnapshotId}");
    Console.WriteLine($"Local World ID: {evidence.LocalWorldId}");
    if (evidence.CreatorWorldId is not null && evidence.Role == ViewerRole)
    {
        Console.WriteLine($"Creator World ID: {evidence.CreatorWorldId}");
    }

    Console.WriteLine($"Evidence: {outputPath}");
    Console.WriteLine();
    Console.WriteLine("[OK] Machine/build/file/local-identity evidence is internally consistent.");
    Console.WriteLine("This probe does not inspect Factorio save contents and does not mark the physical acceptance PASS.");
    Console.WriteLine("The creator-marker, viewer-persistence, and creator-independence observations still require the real game on both PCs.");
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Acceptance evidence collection was cancelled.");
    Environment.ExitCode = 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Acceptance evidence failed: {exception.Message}");
    Environment.ExitCode = 1;
}

static void PrintUsage()
{
    Console.WriteLine("Safe World portable World two-PC acceptance evidence probe");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project tools/SharedWorlds.PortableWorldProbe -- --creator --file <World.safeworld> --build-manifest <acceptance-build.json> --output <pc-a-evidence.json>");
    Console.WriteLine("  dotnet run --project tools/SharedWorlds.PortableWorldProbe -- --viewer --file <World.safeworld> --build-manifest <acceptance-build.json> --creator-evidence <pc-a-evidence.json> --output <pc-b-evidence.json>");
    Console.WriteLine();
    Console.WriteLine("The probe is read-only. It records deterministic physical-test evidence but never launches Factorio, mutates a World, or declares the physical milestone complete.");
}

static void RequireWindows()
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException(
            "Creator/viewer physical acceptance evidence must be collected on the Windows PCs used for the test.");
    }
}

static Dictionary<string, string> ParseOptions(string[] arguments)
{
    if (arguments.Length == 0 || arguments.Length % 2 != 0)
    {
        throw new ArgumentException("Every acceptance option must be supplied as an option/value pair.");
    }

    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        var name = arguments[index];
        var value = arguments[index + 1];
        if (!name.StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Every acceptance option must be supplied as an option/value pair.");
        }

        if (!options.TryAdd(name, value.Trim()))
        {
            throw new ArgumentException($"Option '{name}' was supplied more than once.");
        }
    }

    return options;
}

static void RequireOnlyOptions(
    IReadOnlyDictionary<string, string> options,
    IReadOnlyCollection<string> required)
{
    var allowed = required.ToHashSet(StringComparer.OrdinalIgnoreCase);
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

static string RequireExistingFile(
    IReadOnlyDictionary<string, string> options,
    string option,
    string? requiredExtension = null)
{
    var path = Path.GetFullPath(options[option]);
    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"File supplied for {option} does not exist.", path);
    }

    if (requiredExtension is not null &&
        !string.Equals(Path.GetExtension(path), requiredExtension, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException($"File supplied for {option} must use the '{requiredExtension}' extension.");
    }

    return path;
}

static string RequireOutputPath(
    IReadOnlyDictionary<string, string> options,
    string option)
{
    var path = Path.GetFullPath(options[option]);
    if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException($"File supplied for {option} must use the '.json' extension.");
    }

    return path;
}

static async Task<AcceptanceBuildEvidence> InspectAcceptanceBuildAsync(
    string manifestPath,
    JsonSerializerOptions jsonOptions,
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
                       jsonOptions,
                       cancellationToken)
                   ?? throw new InvalidDataException("Safe World acceptance-build manifest is empty.");
    }

    if (!string.Equals(manifest.DocumentType, AcceptanceBuildDocumentType, StringComparison.Ordinal) ||
        manifest.SchemaVersion != AcceptanceBuildSchemaVersion)
    {
        throw new InvalidDataException("Unsupported Safe World acceptance-build manifest.");
    }

    if (!IsSha1(manifest.CommitSha) || string.Equals(manifest.CommitSha, "unknown", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException("Acceptance-build manifest does not contain an exact 40-character commit SHA.");
    }

    if (string.IsNullOrWhiteSpace(manifest.Executable) ||
        !string.Equals(Path.GetFileName(manifest.Executable), manifest.Executable, StringComparison.Ordinal))
    {
        throw new InvalidDataException("Acceptance-build manifest has an invalid desktop executable name.");
    }

    if (manifest.Files is null || manifest.Files.Count == 0)
    {
        throw new InvalidDataException("Acceptance-build manifest contains no package files.");
    }

    var packageRoot = Path.GetDirectoryName(manifestPath)
                      ?? throw new InvalidDataException("Acceptance-build manifest has no containing package directory.");
    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var verifiedFiles = new List<VerifiedPackageFile>(manifest.Files.Count);

    foreach (var file in manifest.Files)
    {
        if (string.IsNullOrWhiteSpace(file.Path) || file.ByteSize < 0 || !IsSha256(file.Sha256))
        {
            throw new InvalidDataException("Acceptance-build manifest contains an invalid package-file entry.");
        }

        var normalizedPath = file.Path.Replace('\\', '/');
        if (!seenPaths.Add(normalizedPath))
        {
            throw new InvalidDataException($"Acceptance-build manifest contains duplicate package path '{normalizedPath}'.");
        }

        var fullPath = ResolvePackageFile(packageRoot, normalizedPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Acceptance package file '{normalizedPath}' is missing beside the build manifest.",
                fullPath);
        }

        var info = new FileInfo(fullPath);
        if (info.Length != file.ByteSize)
        {
            throw new InvalidDataException(
                $"Acceptance package file '{normalizedPath}' does not match its declared byte size.");
        }

        var actualHash = await ComputeFileSha256Async(fullPath, cancellationToken);
        if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Acceptance package file '{normalizedPath}' does not match its declared SHA-256.");
        }

        verifiedFiles.Add(new VerifiedPackageFile(normalizedPath, info.Length, actualHash));
    }

    var desktop = verifiedFiles.SingleOrDefault(file =>
        string.Equals(file.Path, manifest.Executable, StringComparison.OrdinalIgnoreCase));
    if (desktop is null)
    {
        throw new InvalidDataException(
            "Acceptance-build manifest does not contain the declared Safe World desktop executable.");
    }

    var manifestHash = await ComputeFileSha256Async(manifestPath, cancellationToken);
    var packageFingerprint = ComputePackageFingerprint(verifiedFiles);
    return new AcceptanceBuildEvidence(
        manifest.CommitSha.ToLowerInvariant(),
        manifestHash,
        packageFingerprint,
        desktop.Sha256);
}

static string ResolvePackageFile(string packageRoot, string relativePath)
{
    var platformRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
    if (Path.IsPathRooted(platformRelativePath))
    {
        throw new InvalidDataException("Acceptance-build manifest contains a rooted package path.");
    }

    var fullPath = Path.GetFullPath(Path.Combine(packageRoot, platformRelativePath));
    var relative = Path.GetRelativePath(packageRoot, fullPath);
    if (Path.IsPathRooted(relative) ||
        string.Equals(relative, "..", StringComparison.Ordinal) ||
        relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
    {
        throw new InvalidDataException("Acceptance-build manifest contains a package path outside its package directory.");
    }

    return fullPath;
}

static string ComputePackageFingerprint(IEnumerable<VerifiedPackageFile> files)
{
    var canonical = new StringBuilder();
    foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
    {
        canonical.Append(file.Path);
        canonical.Append('\t');
        canonical.Append(file.ByteSize);
        canonical.Append('\t');
        canonical.Append(file.Sha256);
        canonical.Append('\n');
    }

    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
        .ToLowerInvariant();
}

static async Task<PortableArtifactEvidence> InspectPortableWorldAsync(
    string artifactPath,
    CancellationToken cancellationToken)
{
    PortableWorldPreflight preflight;
    await using (var input = new FileStream(
                     artifactPath,
                     FileMode.Open,
                     FileAccess.Read,
                     FileShare.Read,
                     128 * 1024,
                     FileOptions.Asynchronous | FileOptions.SequentialScan))
    {
        preflight = await PortableWorldArchiveInspector.InspectAsync(input, cancellationToken: cancellationToken);
    }

    if (!string.Equals(preflight.GameAdapterId, "factorio", StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"This physical acceptance probe is qualified only for Factorio, not '{preflight.GameAdapterId}'.");
    }

    var artifactHash = await ComputeFileSha256Async(artifactPath, cancellationToken);
    return new PortableArtifactEvidence(preflight, artifactHash);
}

static async Task<int> RequireExactFactorioEnvironmentAsync(
    FactorioAdapter adapter,
    EnvironmentManifest requiredEnvironment,
    CancellationToken cancellationToken)
{
    var installations = await adapter.DiscoverInstallationsAsync(cancellationToken);
    var readyCount = 0;
    var failureReasons = new List<string>();

    foreach (var installation in installations)
    {
        try
        {
            var verification = await adapter.VerifyEnvironmentAsync(
                installation,
                requiredEnvironment,
                cancellationToken);
            if (verification.IsReady)
            {
                readyCount++;
                continue;
            }

            failureReasons.AddRange(verification.Issues.Select(issue => issue.Message));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failureReasons.Add($"{installation.Source}: {exception.Message}");
        }
    }

    if (readyCount > 0)
    {
        return readyCount;
    }

    var detail = failureReasons.Count == 0
        ? "No Factorio installation was detected."
        : string.Join(" | ", failureReasons.Distinct(StringComparer.Ordinal).Take(5));
    throw new InvalidOperationException(
        $"No installed Factorio environment exactly reproduces the portable World's requirements. {detail}");
}

static void RequireVanillaFactorio(EnvironmentManifest environment)
{
    var nonBuiltInMods = environment.Components
        .Where(component =>
            string.Equals(component.Kind, "mod", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(component.Source, "builtin", StringComparison.OrdinalIgnoreCase))
        .Select(component => component.Id)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (nonBuiltInMods.Length > 0)
    {
        throw new InvalidOperationException(
            $"Factorio V1 physical acceptance requires vanilla state with no user mods. Found: {string.Join(", ", nonBuiltInMods)}.");
    }
}

static async Task<World> FindCreatorWorldAsync(
    string snapshotId,
    CancellationToken cancellationToken)
{
    var storage = new LocalWorldStorage(GetSafeWorldStorageRoot());
    var worlds = await storage.ListWorldsAsync(cancellationToken);
    var matches = worlds
        .Where(world =>
            string.Equals(world.GameAdapterId, "factorio", StringComparison.Ordinal) &&
            world.CurrentStateRevisionId is { } currentStateId &&
            string.Equals(currentStateId.ToString(), snapshotId, StringComparison.Ordinal))
        .ToArray();

    return matches.Length == 1
        ? matches[0]
        : throw new InvalidOperationException(
            $"Expected exactly one local creator World whose current state revision is portable snapshot '{snapshotId}', but found {matches.Length}.");
}

static async Task<World> FindViewerWorldAsync(
    string snapshotId,
    CancellationToken cancellationToken)
{
    var storage = new LocalWorldStorage(GetSafeWorldStorageRoot());
    var worlds = await storage.ListWorldsAsync(cancellationToken);
    var matches = worlds
        .Where(world =>
            string.Equals(world.GameAdapterId, "factorio", StringComparison.Ordinal) &&
            world.StartedFrom is { } startedFrom &&
            string.Equals(startedFrom.SnapshotId, snapshotId, StringComparison.Ordinal))
        .ToArray();

    return matches.Length == 1
        ? matches[0]
        : throw new InvalidOperationException(
            $"Expected exactly one local viewer World started from portable snapshot '{snapshotId}', but found {matches.Length}.");
}

static string GetSafeWorldStorageRoot()
{
    var localDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrWhiteSpace(localDataRoot))
    {
        localDataRoot = Path.GetTempPath();
    }

    return Path.Combine(localDataRoot, "SharedWorlds", "data");
}

static void RequireIndependentViewerWorld(
    World viewerWorld,
    PortableWorldAcceptanceEvidence creatorEvidence,
    PortableWorldPreflight preflight)
{
    if (string.Equals(viewerWorld.Id.ToString(), creatorEvidence.LocalWorldId, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Viewer import reused the creator's canonical World ID.");
    }

    if (viewerWorld.SharingMode != WorldSharingMode.LocalOnly || viewerWorld.Visibility != WorldVisibility.Private)
    {
        throw new InvalidOperationException("Viewer portable World is not local-only and private.");
    }

    if (viewerWorld.Members.Count != 1)
    {
        throw new InvalidOperationException(
            $"Viewer portable World must have exactly one local owner/member, but found {viewerWorld.Members.Count}.");
    }

    var localMember = viewerWorld.Members[0];
    if (!string.Equals(localMember.Provider, "local", StringComparison.Ordinal) ||
        !string.Equals(localMember.ExternalId, Environment.UserName, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Viewer portable World is not owned by the current local Windows user.");
    }

    if (viewerWorld.StartedFrom is null ||
        !string.Equals(viewerWorld.StartedFrom.SnapshotId, preflight.SnapshotId, StringComparison.Ordinal) ||
        !string.Equals(viewerWorld.StartedFrom.WorldName, preflight.WorldName, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Viewer portable World did not preserve the expected source provenance.");
    }

    if (viewerWorld.CurrentStateRevisionId is null ||
        string.Equals(viewerWorld.CurrentStateRevisionId.Value.ToString(), preflight.SnapshotId, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Viewer portable World did not receive a fresh local state-revision identity.");
    }
}

static int CountDesktopProcesses()
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

static PortableWorldAcceptanceEvidence CreateCreatorEvidence(
    AcceptanceBuildEvidence build,
    PortableArtifactEvidence artifact,
    int exactFactorioInstallationCount,
    World creatorWorld)
    => new(
        SchemaVersion: EvidenceSchemaVersion,
        Role: CreatorRole,
        RecordedAtUtc: DateTimeOffset.UtcNow,
        SafeWorldCommitSha: build.CommitSha,
        AcceptanceBuildManifestSha256: build.ManifestSha256,
        PackageFingerprintSha256: build.PackageFingerprintSha256,
        DesktopExecutableSha256: build.DesktopExecutableSha256,
        ArtifactSha256: artifact.Sha256,
        SnapshotId: artifact.Preflight.SnapshotId,
        PortableWorldCreatedAtUtc: artifact.Preflight.CreatedAt,
        WorldName: artifact.Preflight.WorldName,
        RequiredFactorioVersion: artifact.Preflight.Environment.GameVersion,
        ExactFactorioInstallationCount: exactFactorioInstallationCount,
        VanillaUserModsAbsent: true,
        LocalWorldId: creatorWorld.Id.ToString(),
        CreatorWorldId: creatorWorld.Id.ToString(),
        LocalOnly: creatorWorld.SharingMode == WorldSharingMode.LocalOnly,
        Private: creatorWorld.Visibility == WorldVisibility.Private,
        LocalOwnerMatchesCurrentUser: null,
        StartedFromSnapshotMatches: null,
        FreshLocalStateRevision: null,
        SafeWorldDesktopProcessCount: null);

static PortableWorldAcceptanceEvidence CreateViewerEvidence(
    AcceptanceBuildEvidence build,
    PortableArtifactEvidence artifact,
    int exactFactorioInstallationCount,
    PortableWorldAcceptanceEvidence creatorEvidence,
    World viewerWorld,
    int desktopProcessCount)
    => new(
        SchemaVersion: EvidenceSchemaVersion,
        Role: ViewerRole,
        RecordedAtUtc: DateTimeOffset.UtcNow,
        SafeWorldCommitSha: build.CommitSha,
        AcceptanceBuildManifestSha256: build.ManifestSha256,
        PackageFingerprintSha256: build.PackageFingerprintSha256,
        DesktopExecutableSha256: build.DesktopExecutableSha256,
        ArtifactSha256: artifact.Sha256,
        SnapshotId: artifact.Preflight.SnapshotId,
        PortableWorldCreatedAtUtc: artifact.Preflight.CreatedAt,
        WorldName: artifact.Preflight.WorldName,
        RequiredFactorioVersion: artifact.Preflight.Environment.GameVersion,
        ExactFactorioInstallationCount: exactFactorioInstallationCount,
        VanillaUserModsAbsent: true,
        LocalWorldId: viewerWorld.Id.ToString(),
        CreatorWorldId: creatorEvidence.LocalWorldId,
        LocalOnly: viewerWorld.SharingMode == WorldSharingMode.LocalOnly,
        Private: viewerWorld.Visibility == WorldVisibility.Private,
        LocalOwnerMatchesCurrentUser: true,
        StartedFromSnapshotMatches: true,
        FreshLocalStateRevision: true,
        SafeWorldDesktopProcessCount: desktopProcessCount);

static async Task<PortableWorldAcceptanceEvidence> ReadCreatorEvidenceAsync(
    string path,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken)
{
    await using var input = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    var evidence = await JsonSerializer.DeserializeAsync<PortableWorldAcceptanceEvidence>(
                       input,
                       jsonOptions,
                       cancellationToken)
                   ?? throw new InvalidDataException("Creator evidence file is empty.");

    if (evidence.SchemaVersion != EvidenceSchemaVersion ||
        !string.Equals(evidence.Role, CreatorRole, StringComparison.Ordinal))
    {
        throw new InvalidDataException("Creator evidence file has an unsupported schema or role.");
    }

    if (!IsSha1(evidence.SafeWorldCommitSha) ||
        !IsSha256(evidence.PackageFingerprintSha256) ||
        !IsSha256(evidence.DesktopExecutableSha256) ||
        !IsSha256(evidence.ArtifactSha256) ||
        string.IsNullOrWhiteSpace(evidence.SnapshotId) ||
        string.IsNullOrWhiteSpace(evidence.LocalWorldId))
    {
        throw new InvalidDataException("Creator evidence file is incomplete or malformed.");
    }

    return evidence;
}

static void RequireSameCreatorInputs(
    PortableWorldAcceptanceEvidence creatorEvidence,
    AcceptanceBuildEvidence build,
    PortableArtifactEvidence artifact)
{
    RequireEqual("Safe World commit", creatorEvidence.SafeWorldCommitSha, build.CommitSha);
    RequireEqual(
        "Safe World package fingerprint",
        creatorEvidence.PackageFingerprintSha256,
        build.PackageFingerprintSha256);
    RequireEqual(
        "Safe World desktop executable hash",
        creatorEvidence.DesktopExecutableSha256,
        build.DesktopExecutableSha256);
    RequireEqual("portable World SHA-256", creatorEvidence.ArtifactSha256, artifact.Sha256);
    RequireEqual("portable snapshot ID", creatorEvidence.SnapshotId, artifact.Preflight.SnapshotId);
    RequireEqual(
        "Factorio version",
        creatorEvidence.RequiredFactorioVersion,
        artifact.Preflight.Environment.GameVersion);
}

static void RequireEqual(string description, string creatorValue, string viewerValue)
{
    if (!string.Equals(creatorValue, viewerValue, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"PC A and PC B do not use the same {description}. Creator='{creatorValue}', viewer='{viewerValue}'.");
    }
}

static async Task WriteEvidenceAsync(
    string outputPath,
    PortableWorldAcceptanceEvidence evidence,
    JsonSerializerOptions jsonOptions,
    CancellationToken cancellationToken)
{
    var directory = Path.GetDirectoryName(outputPath)
                    ?? throw new InvalidOperationException("Evidence output path has no containing directory.");
    Directory.CreateDirectory(directory);

    var temporaryPath = Path.Combine(
        directory,
        $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
    try
    {
        await using (var output = new FileStream(
                         temporaryPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(output, evidence, jsonOptions, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, outputPath, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }
    }
}

static async Task<string> ComputeFileSha256Async(
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
    using var sha256 = SHA256.Create();
    var hash = await sha256.ComputeHashAsync(input, cancellationToken);
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static bool IsSha1(string? value)
    => value is { Length: 40 } && value.All(Uri.IsHexDigit);

static bool IsSha256(string? value)
    => value is { Length: 64 } && value.All(Uri.IsHexDigit);

sealed record AcceptanceBuildManifest(
    string DocumentType,
    int SchemaVersion,
    string CommitSha,
    DateTimeOffset BuiltAtUtc,
    string Runtime,
    string Configuration,
    bool SelfContained,
    string Executable,
    string SteamNativeRuntime,
    IReadOnlyList<AcceptanceBuildFile> Files);

sealed record AcceptanceBuildFile(
    string Path,
    long ByteSize,
    string Sha256);

sealed record VerifiedPackageFile(
    string Path,
    long ByteSize,
    string Sha256);

sealed record AcceptanceBuildEvidence(
    string CommitSha,
    string ManifestSha256,
    string PackageFingerprintSha256,
    string DesktopExecutableSha256);

sealed record PortableArtifactEvidence(
    PortableWorldPreflight Preflight,
    string Sha256);

sealed record PortableWorldAcceptanceEvidence(
    int SchemaVersion,
    string Role,
    DateTimeOffset RecordedAtUtc,
    string SafeWorldCommitSha,
    string AcceptanceBuildManifestSha256,
    string PackageFingerprintSha256,
    string DesktopExecutableSha256,
    string ArtifactSha256,
    string SnapshotId,
    DateTimeOffset PortableWorldCreatedAtUtc,
    string WorldName,
    string RequiredFactorioVersion,
    int ExactFactorioInstallationCount,
    bool VanillaUserModsAbsent,
    string LocalWorldId,
    string? CreatorWorldId,
    bool LocalOnly,
    bool Private,
    bool? LocalOwnerMatchesCurrentUser,
    bool? StartedFromSnapshotMatches,
    bool? FreshLocalStateRevision,
    int? SafeWorldDesktopProcessCount);
