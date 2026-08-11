using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.PeerWorldProbe;

internal static class Program
{
    private const string EvidenceDocumentType = "steward.peer-two-pc-evidence";
    private const string AcceptanceDocumentType = "steward.e4-desktop-acceptance-build";
    private const string DeviceSettingsDocumentType = "sharedworlds.device-settings";

    private static readonly JsonSerializerOptions OutputJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Any(arg => arg is "--help" or "-h" or "/?"))
            {
                PrintHelp();
                return 0;
            }

            var parsed = ParseArguments(args);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException("Windows LocalApplicationData could not be resolved.");
            }

            var sharedWorldsRoot = Path.Combine(localAppData, "SharedWorlds");
            var dataRoot = GetFullPathOption(parsed.Options, "--data-root")
                ?? Path.Combine(sharedWorldsRoot, "data");

            if (parsed.ListWorlds)
            {
                await ListPeerWorldsAsync(dataRoot);
                return 0;
            }

            var packageRoot = GetFullPathOption(parsed.Options, "--package-root")
                ?? ResolveDefaultPackageRoot();
            var package = await VerifyPackageAsync(packageRoot);
            if (parsed.VerifyPackageOnly)
            {
                Console.WriteLine("[OK] Exact AppID-only Steward product package verified.");
                Console.WriteLine($"  Commit: {package.CommitSha}");
                Console.WriteLine($"  Steam AppID: {package.SteamAppId}");
                Console.WriteLine($"  Manifest SHA-256: {package.ManifestSha256}");
                return 0;
            }

            var worldText = RequireOption(parsed.Options, "--world");
            if (!Guid.TryParse(worldText, out var worldGuid) || worldGuid == Guid.Empty)
            {
                throw new ArgumentException("--world must be a non-empty Steward World GUID.");
            }

            var settingsPath = GetFullPathOption(parsed.Options, "--settings")
                ?? Path.Combine(sharedWorldsRoot, "settings", "device.json");
            var outputPath = GetFullPathOption(parsed.Options, "--output")
                ?? Path.GetFullPath($"peer-evidence-{Environment.MachineName}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
            EnsureOutputOutsidePackage(outputPath, packageRoot);

            var installationId = await ReadInstallationIdAsync(settingsPath);
            var storage = new LocalWorldStorage(dataRoot);
            var worldId = new WorldId(worldGuid);
            var world = await storage.LoadWorldAsync(worldId)
                ?? throw new InvalidDataException($"World '{worldId}' is not present in canonical local Steward storage.");

            if (world.SharingMode != WorldSharingMode.Shared || world.PeerAuthority is null)
            {
                throw new InvalidDataException(
                    $"World '{worldId}' is not a persistent peer-shared World with canonical authority.");
            }
            if (world.PeerAuthority.Generation == 0)
            {
                throw new InvalidDataException($"World '{worldId}' has invalid peer authority generation 0.");
            }
            if (world.CurrentStateRevisionId is not { } stateId ||
                world.CurrentEnvironmentRevisionId is not { } environmentId)
            {
                throw new InvalidDataException($"World '{worldId}' does not have a complete canonical state/environment head.");
            }

            var state = await storage.LoadStateRevisionAsync(worldId, stateId)
                ?? throw new InvalidDataException($"Current state revision '{stateId}' is missing.");
            var environment = await storage.LoadEnvironmentRevisionAsync(worldId, environmentId)
                ?? throw new InvalidDataException($"Current environment revision '{environmentId}' is missing.");
            if (state.WorldId != worldId ||
                state.Id != stateId ||
                state.EnvironmentRevisionId != environmentId ||
                environment.WorldId != worldId ||
                environment.Id != environmentId ||
                !string.Equals(state.AdapterId, world.GameAdapterId, StringComparison.Ordinal) ||
                !string.Equals(environment.Manifest.AdapterId, world.GameAdapterId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"World '{worldId}' canonical head metadata is internally inconsistent.");
            }

            await using var payload = await storage.OpenRevisionAsync(worldId, stateId);
            var payloadEvidence = await HashStreamAsync(payload);

            var members = world.Members
                .OrderBy(member => member.Provider, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.ExternalId, StringComparer.Ordinal)
                .Select(member => new IdentityEvidence(
                    member.Provider,
                    member.ExternalId,
                    member.DisplayName))
                .ToArray();
            var authority = world.PeerAuthority;
            var evidence = new PeerEvidence(
                EvidenceDocumentType,
                SchemaVersion: 1,
                CapturedAtUtc: DateTimeOffset.UtcNow,
                MachineName: Environment.MachineName,
                InstallationId: installationId,
                Package: package,
                World: new WorldEvidence(
                    world.Id.ToString(),
                    world.Name,
                    world.GameAdapterId,
                    world.SharingMode.ToString(),
                    members,
                    new AuthorityEvidence(
                        new IdentityEvidence(
                            authority.Holder.Provider,
                            authority.Holder.ExternalId,
                            authority.Holder.DisplayName),
                        authority.Generation),
                    stateId.ToString(),
                    environmentId.ToString(),
                    state.StatePackageId,
                    payloadEvidence.ByteSize,
                    payloadEvidence.Sha256));

            var outputDirectory = Path.GetDirectoryName(outputPath)
                ?? throw new InvalidOperationException("Evidence output path has no containing directory.");
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(
                outputPath,
                JsonSerializer.Serialize(evidence, OutputJson),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Console.WriteLine("[OK] Read-only Steward peer evidence captured.");
            Console.WriteLine($"  World: {world.Id}");
            Console.WriteLine($"  Machine: {Environment.MachineName}");
            Console.WriteLine($"  Installation: {installationId}");
            Console.WriteLine($"  Authority: {authority.Holder.Provider}:{authority.Holder.ExternalId} generation {authority.Generation}");
            Console.WriteLine($"  State: {stateId}");
            Console.WriteLine($"  Payload SHA-256: {payloadEvidence.Sha256}");
            Console.WriteLine($"  Evidence: {outputPath}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[FAIL] {exception.Message}");
            return 1;
        }
    }

    private static async Task ListPeerWorldsAsync(string dataRoot)
    {
        var storage = new LocalWorldStorage(dataRoot);
        var worlds = (await storage.ListWorldsAsync())
            .Where(world => world.SharingMode == WorldSharingMode.Shared && world.PeerAuthority is not null)
            .OrderBy(world => world.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(world => world.Id.ToString(), StringComparer.Ordinal)
            .ToArray();

        if (worlds.Length == 0)
        {
            Console.WriteLine("No persistent peer-shared Steward Worlds exist on this PC.");
            return;
        }

        Console.WriteLine("Persistent peer-shared Steward Worlds:");
        foreach (var world in worlds)
        {
            var authority = world.PeerAuthority!;
            Console.WriteLine(
                $"  {world.Id}  {world.Name}  generation={authority.Generation}  holder={authority.Holder.Provider}:{authority.Holder.ExternalId}");
        }
    }

    private static async Task<PackageEvidence> VerifyPackageAsync(string packageRoot)
    {
        var manifestPath = Path.Combine(packageRoot, "acceptance-build.json");
        var steamPath = Path.Combine(packageRoot, "steward-steam.json");
        if (!File.Exists(manifestPath) || !File.Exists(steamPath))
        {
            throw new InvalidDataException(
                "The peer acceptance product must contain acceptance-build.json and steward-steam.json.");
        }
        if (File.Exists(Path.Combine(packageRoot, "steward-steam-release.json")) ||
            File.Exists(Path.Combine(packageRoot, "steward-friends-build.json")))
        {
            throw new InvalidDataException(
                "The physical peer product must be AppID-only and cannot contain legacy remote migration configuration.");
        }

        using var manifestDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
        var manifest = manifestDocument.RootElement;
        if (!manifest.TryGetProperty("documentType", out var documentType) ||
            !string.Equals(documentType.GetString(), AcceptanceDocumentType, StringComparison.Ordinal) ||
            !manifest.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.GetInt32() != 2 ||
            !manifest.TryGetProperty("commitSha", out var commitElement) ||
            string.IsNullOrWhiteSpace(commitElement.GetString()) ||
            !manifest.TryGetProperty("files", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("acceptance-build.json is not a supported exact Steward package manifest.");
        }

        var expected = new Dictionary<string, (long ByteSize, string Sha256)>(StringComparer.Ordinal);
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            var path = fileElement.GetProperty("path").GetString();
            var byteSize = fileElement.GetProperty("byteSize").GetInt64();
            var sha256 = fileElement.GetProperty("sha256").GetString();
            if (string.IsNullOrWhiteSpace(path) ||
                path.Contains('\\') ||
                Path.IsPathRooted(path) ||
                path.Split('/').Any(segment => segment is "" or "." or "..") ||
                byteSize < 0 ||
                !IsSha256(sha256) ||
                !expected.TryAdd(path, (byteSize, sha256!)))
            {
                throw new InvalidDataException("acceptance-build.json contains an invalid or duplicate file entry.");
            }
        }

        var actualFiles = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (actualFiles.Length != expected.Count)
        {
            throw new InvalidDataException(
                $"Peer product contains {actualFiles.Length} package files but its manifest lists {expected.Count}.");
        }

        foreach (var file in actualFiles)
        {
            var relative = Path.GetRelativePath(packageRoot, file).Replace('\\', '/');
            if (!expected.TryGetValue(relative, out var entry))
            {
                throw new InvalidDataException($"Unmanifested peer-product file found: '{relative}'.");
            }
            var info = new FileInfo(file);
            if (info.Length != entry.ByteSize)
            {
                throw new InvalidDataException($"Peer-product file '{relative}' does not match its manifested byte size.");
            }
            var hash = await HashFileAsync(file);
            if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Peer-product file '{relative}' does not match its manifested SHA-256.");
            }
        }

        using var steamDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(steamPath));
        var steam = steamDocument.RootElement;
        var steamProperties = steam.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray();
        if (!steamProperties.SequenceEqual(new[] { "schemaVersion", "steamAppId" }.OrderBy(name => name), StringComparer.Ordinal) ||
            steam.GetProperty("schemaVersion").GetInt32() != 1 ||
            !steam.GetProperty("steamAppId").TryGetUInt32(out var steamAppId) ||
            steamAppId == 0)
        {
            throw new InvalidDataException("steward-steam.json is not the expected AppID-only peer configuration.");
        }

        return new PackageEvidence(
            commitElement.GetString()!,
            await HashFileAsync(manifestPath),
            steamAppId);
    }

    private static async Task<string> ReadInstallationIdAsync(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            throw new FileNotFoundException("Steward device settings do not exist on this PC.", settingsPath);
        }

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(settingsPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("documentType", out var documentType) ||
            !string.Equals(documentType.GetString(), DeviceSettingsDocumentType, StringComparison.Ordinal) ||
            !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.GetInt32() != 2 ||
            !root.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("installationId", out var installationElement))
        {
            throw new InvalidDataException("Steward device settings are not the current installation-bound schema.");
        }

        var installationId = installationElement.GetString();
        if (string.IsNullOrWhiteSpace(installationId) ||
            installationId.Length > 128 ||
            installationId.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("Steward device settings contain an invalid installation identity.");
        }

        return installationId;
    }

    private static async Task<(long ByteSize, string Sha256)> HashStreamAsync(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            total += read;
        }

        return (total, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            useAsync: true);
        return (await HashStreamAsync(stream)).Sha256;
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static ParsedArguments ParseArguments(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var listWorlds = false;
        var verifyPackageOnly = false;

        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name == "--list")
            {
                if (listWorlds)
                {
                    throw new ArgumentException("--list was supplied more than once.");
                }
                listWorlds = true;
                continue;
            }
            if (name == "--verify-package")
            {
                if (verifyPackageOnly)
                {
                    throw new ArgumentException("--verify-package was supplied more than once.");
                }
                verifyPackageOnly = true;
                continue;
            }

            if (name is not ("--world" or "--output" or "--package-root" or "--data-root" or "--settings") ||
                index + 1 >= args.Length ||
                string.IsNullOrWhiteSpace(args[index + 1]) ||
                !options.TryAdd(name, args[index + 1]))
            {
                throw new ArgumentException($"Unsupported, missing, or duplicate probe option '{name}'.");
            }
            index++;
        }

        if (listWorlds && verifyPackageOnly)
        {
            throw new ArgumentException("--list and --verify-package are mutually exclusive.");
        }
        if (listWorlds && options.Keys.Any(key => key is not "--data-root"))
        {
            throw new ArgumentException("--list accepts only the optional --data-root override.");
        }
        if (verifyPackageOnly && options.Keys.Any(key => key is not "--package-root"))
        {
            throw new ArgumentException("--verify-package accepts only the optional --package-root override.");
        }

        return new ParsedArguments(options, listWorlds, verifyPackageOnly);
    }

    private static string RequireOption(IReadOnlyDictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value)
            ? value
            : throw new ArgumentException($"Required option {name} is missing.");

    private static string? GetFullPathOption(IReadOnlyDictionary<string, string> options, string name)
        => options.TryGetValue(name, out var value) ? Path.GetFullPath(value) : null;

    private static string ResolveDefaultPackageRoot()
    {
        var toolDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var kitRoot = Directory.GetParent(toolDirectory)
            ?? throw new InvalidOperationException("Could not resolve peer acceptance kit root from probe location.");
        return Path.Combine(kitRoot.FullName, "product");
    }

    private static void EnsureOutputOutsidePackage(string outputPath, string packageRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot)) + Path.DirectorySeparatorChar;
        var output = Path.GetFullPath(outputPath);
        if (output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Evidence output must be outside the byte-verifiable peer product directory so the package remains immutable.");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Steward peer two-PC read-only evidence probe");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  SharedWorlds.PeerWorldProbe.exe --list");
        Console.WriteLine("  SharedWorlds.PeerWorldProbe.exe --verify-package [--package-root <dir>]");
        Console.WriteLine("  SharedWorlds.PeerWorldProbe.exe --world <WorldGuid> [--output <json>] [--package-root <dir>] [--data-root <dir>] [--settings <device.json>]");
        Console.WriteLine();
        Console.WriteLine("The probe reads canonical local Steward storage, verifies the exact AppID-only product, hashes the current state payload, and writes evidence outside product/. It does not initialize Steam, contact a backend, mutate World state, or change authority.");
    }

    private sealed record ParsedArguments(
        IReadOnlyDictionary<string, string> Options,
        bool ListWorlds,
        bool VerifyPackageOnly);

    private sealed record IdentityEvidence(
        string Provider,
        string ExternalId,
        string? DisplayName);

    private sealed record AuthorityEvidence(
        IdentityEvidence Holder,
        ulong Generation);

    private sealed record PackageEvidence(
        string CommitSha,
        string ManifestSha256,
        uint SteamAppId);

    private sealed record WorldEvidence(
        string Id,
        string Name,
        string GameAdapterId,
        string SharingMode,
        IReadOnlyList<IdentityEvidence> Members,
        AuthorityEvidence Authority,
        string CurrentStateRevisionId,
        string CurrentEnvironmentRevisionId,
        string StatePackageId,
        long PayloadByteSize,
        string PayloadSha256);

    private sealed record PeerEvidence(
        string DocumentType,
        int SchemaVersion,
        DateTimeOffset CapturedAtUtc,
        string MachineName,
        string InstallationId,
        PackageEvidence Package,
        WorldEvidence World);
}
