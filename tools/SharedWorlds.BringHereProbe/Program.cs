using SharedWorlds.Core.Domain;

namespace SharedWorlds.BringHereProbe;

internal static class Program
{
    private const int UsageError = 2;

    public static async Task<int> Main(string[] args)
    {
        if (args.Any(argument =>
                string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(argument, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            PrintUsage();
            return 0;
        }

        if (args.Length == 1 && string.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
        {
            return await ProbeSelfTest.RunAsync();
        }

        if (args.Length == 0 || !TryRole(args[0], out var role))
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
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Physical Bring Here evidence must be collected on the Windows PCs used for the test.");
            }

            var options = ParseOptions(args[1..]);
            ValidateOptions(role, options);
            var worldId = ParseWorldId(options["--world-id"]);
            var manifestPath = ExistingFile(options["--build-manifest"]);
            var outputPath = OutputFile(options["--output"]);
            var safeWorldRoot = options.TryGetValue("--safe-world-root", out var root)
                ? Path.GetFullPath(root)
                : ProbeEngine.GetDefaultSafeWorldRoot();
            var processCount = ProbeEngine.CountDesktopProcesses();
            if (processCount != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one running Safe World desktop process, but found {processCount}.");
            }

            var build = await ProbeEngine.InspectAcceptanceBuildAsync(manifestPath, cancellation.Token);
            var snapshot = await ProbeEngine.InspectLocalSnapshotAsync(
                safeWorldRoot,
                worldId,
                cancellation.Token);
            BringHereAcceptanceEvidence evidence;

            if (role == ProbeContract.SourceBeforeRole)
            {
                evidence = ProbeEngine.CreateEvidence(
                    role,
                    processCount,
                    build,
                    snapshot,
                    Environment.MachineName,
                    null,
                    null);
            }
            else if (role == ProbeContract.TargetAfterRole)
            {
                var sourcePath = ExistingFile(options["--source-evidence"]);
                var source = await ProbeEngine.ReadEvidenceAsync(
                    sourcePath,
                    ProbeContract.SourceBeforeRole,
                    requirePhysicalProcess: true,
                    cancellation.Token);
                var sourceHash = await ProbeEngine.ComputeFileSha256Async(sourcePath, cancellation.Token);
                evidence = ProbeEngine.CreateEvidence(
                    role,
                    processCount,
                    build,
                    snapshot,
                    Environment.MachineName,
                    sourceHash,
                    null);
                ProbeEngine.RequireTargetMatchesSource(source, evidence);
            }
            else
            {
                var sourcePath = ExistingFile(options["--source-evidence"]);
                var targetPath = ExistingFile(options["--target-evidence"]);
                var source = await ProbeEngine.ReadEvidenceAsync(
                    sourcePath,
                    ProbeContract.SourceBeforeRole,
                    requirePhysicalProcess: true,
                    cancellation.Token);
                var target = await ProbeEngine.ReadEvidenceAsync(
                    targetPath,
                    ProbeContract.TargetAfterRole,
                    requirePhysicalProcess: true,
                    cancellation.Token);
                var sourceHash = await ProbeEngine.ComputeFileSha256Async(sourcePath, cancellation.Token);
                var targetHash = await ProbeEngine.ComputeFileSha256Async(targetPath, cancellation.Token);
                evidence = ProbeEngine.CreateEvidence(
                    role,
                    processCount,
                    build,
                    snapshot,
                    Environment.MachineName,
                    sourceHash,
                    targetHash);
                ProbeEngine.RequireSourceStillExact(
                    source,
                    target,
                    evidence,
                    sourceHash,
                    targetHash);
            }

            await ProbeEngine.WriteEvidenceAsync(outputPath, evidence, cancellation.Token);
            PrintSummary(evidence, outputPath);
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

    private static bool TryRole(string value, out string role)
    {
        role = value.ToLowerInvariant() switch
        {
            "--source-before" => ProbeContract.SourceBeforeRole,
            "--target-after" => ProbeContract.TargetAfterRole,
            "--source-after" => ProbeContract.SourceAfterRole,
            _ => string.Empty
        };
        return role.Length > 0;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length == 0 || args.Length % 2 != 0)
        {
            throw new ArgumentException("Every option must be supplied as an option/value pair.");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(args[index + 1]) ||
                !result.TryAdd(args[index], args[index + 1].Trim()))
            {
                throw new ArgumentException("Probe options are malformed or duplicated.");
            }
        }

        return result;
    }

    private static void ValidateOptions(string role, IReadOnlyDictionary<string, string> options)
    {
        var required = role switch
        {
            ProbeContract.SourceBeforeRole => new[] { "--world-id", "--build-manifest", "--output" },
            ProbeContract.TargetAfterRole => new[] { "--world-id", "--build-manifest", "--source-evidence", "--output" },
            _ => new[] { "--world-id", "--build-manifest", "--source-evidence", "--target-evidence", "--output" }
        };
        var allowed = required.Append("--safe-world-root").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (options.Keys.Any(option => !allowed.Contains(option)) ||
            required.Any(option => !options.ContainsKey(option)))
        {
            throw new ArgumentException("Probe options do not match the selected evidence role.");
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

    private static string ExistingFile(string value)
    {
        var path = Path.GetFullPath(value);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Required probe input does not exist.", path);
    }

    private static string OutputFile(string value)
    {
        var path = Path.GetFullPath(value);
        return string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
            ? path
            : throw new ArgumentException("Evidence output must use the .json extension.");
    }

    private static void PrintSummary(BringHereAcceptanceEvidence evidence, string outputPath)
    {
        Console.WriteLine("Safe World private Bring Here two-PC acceptance evidence");
        Console.WriteLine($"Role: {evidence.Role}");
        Console.WriteLine($"Machine: {evidence.MachineName}");
        Console.WriteLine($"Installation: {evidence.InstallationId}");
        Console.WriteLine($"Commit: {evidence.Build.CommitSha}");
        Console.WriteLine($"World: {evidence.World.WorldId}");
        Console.WriteLine($"State: {evidence.World.StateRevisionId}");
        Console.WriteLine($"Environment: {evidence.World.EnvironmentRevisionId}");
        Console.WriteLine($"Payload SHA-256: {evidence.World.PayloadSha256}");
        Console.WriteLine($"Evidence: {outputPath}");
        Console.WriteLine("[OK] Local immutable and publication evidence are internally consistent.");
        Console.WriteLine("The required UI observations still determine the physical PASS.");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Safe World private Bring Here two-PC acceptance evidence probe");
        Console.WriteLine("  --source-before --world-id <id> --build-manifest <json> --output <pc-a-before.json>");
        Console.WriteLine("  --target-after --world-id <id> --build-manifest <json> --source-evidence <pc-a-before.json> --output <pc-b-after.json>");
        Console.WriteLine("  --source-after --world-id <id> --build-manifest <json> --source-evidence <pc-a-before.json> --target-evidence <pc-b-after.json> --output <pc-a-after.json>");
        Console.WriteLine("Optional: --safe-world-root <path>");
        Console.WriteLine("Validation: --self-test");
        Console.WriteLine("The probe is read-only outside --self-test and never declares the physical test complete by itself.");
    }
}
