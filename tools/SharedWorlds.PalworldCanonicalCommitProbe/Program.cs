using System.Diagnostics;
using SharedWorlds.Core.State;
using SharedWorlds.GameAdapters.Palworld;
using SharedWorlds.Infrastructure.State;

Console.WriteLine("SharedWorlds Palworld canonical state commit probe");
Console.WriteLine();

if (IsProcessRunning("PalServer"))
{
    Console.Error.WriteLine("PalServer is still running.");
    Console.Error.WriteLine("Stop it cleanly before capturing and committing canonical state.");
    Environment.ExitCode = 1;
    return;
}

var adapter = new PalworldAdapter();
var installations = await adapter.DiscoverInstallationsAsync();
var installation = installations.FirstOrDefault(candidate =>
    candidate.Metadata is not null &&
    candidate.Metadata.ContainsKey("dedicatedServerExecutablePath"));

if (installation is null)
{
    Console.Error.WriteLine("No Palworld installation with an available dedicated server was detected.");
    Environment.ExitCode = 1;
    return;
}

var worlds = await adapter.DiscoverWorldsAsync(installation);
var dedicatedWorld = worlds
    .Where(world => world.Id.StartsWith("dedicated:", StringComparison.OrdinalIgnoreCase))
    .OrderByDescending(world => GetLastWriteTimeUtcSafe(Path.Combine(world.SourcePath, "Level.sav")))
    .FirstOrDefault();

if (dedicatedWorld is null)
{
    Console.Error.WriteLine("No dedicated Palworld world was detected for canonical capture.");
    Environment.ExitCode = 1;
    return;
}

var nativeWorldId = Path.GetFileName(
    Path.TrimEndingDirectorySeparator(dedicatedWorld.SourcePath));
if (string.IsNullOrWhiteSpace(nativeWorldId))
{
    Console.Error.WriteLine("Could not derive the native Palworld world id.");
    Environment.ExitCode = 1;
    return;
}

var worldId = $"palworld:{nativeWorldId}";
var store = new FileSystemCanonicalWorldStateStore();
var coordinator = new CanonicalWorldStateCommitCoordinator(store);
var previousHead = await coordinator.ReadHeadAsync(worldId);

Console.WriteLine($"World: {worldId}");
Console.WriteLine($"Source: {dedicatedWorld.SourcePath}");
Console.WriteLine($"Canonical store: {store.RootPath}");
Console.WriteLine($"Expected head: {previousHead?.RevisionId ?? "(none)"}");
Console.WriteLine();
Console.WriteLine("Capturing candidate state...");

var captured = await adapter.CaptureDetectedWorldAsync(installation, dedicatedWorld);
Console.WriteLine($"Candidate package: {captured.Package.Path}");
Console.WriteLine($"Delete after durable store: {captured.DeletePackageAfterStore}");
Console.WriteLine("Committing candidate with compare-and-swap...");

var result = await coordinator.CommitAsync(
    worldId,
    previousHead?.RevisionId,
    captured);

Console.WriteLine();
Console.WriteLine($"Commit status: {result.Status}");
Console.WriteLine($"Advanced head: {result.AdvancedHead}");
Console.WriteLine($"Succeeded: {result.Succeeded}");
Console.WriteLine($"Observed head before commit: {result.ObservedHeadRevisionId ?? "(none)"}");
Console.WriteLine($"Canonical revision: {result.Head?.RevisionId ?? "(none)"}");
Console.WriteLine($"Parent revision: {result.Head?.ParentRevisionId ?? "(none)"}");
Console.WriteLine($"Durable package: {result.Head?.PackagePath ?? "(none)"}");
Console.WriteLine($"Durable package exists: {File.Exists(result.Head?.PackagePath)}");
Console.WriteLine($"Temporary candidate remains: {File.Exists(captured.Package.Path)}");

if (!result.Succeeded || result.Head is null || !File.Exists(result.Head.PackagePath))
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Canonical state commit did not produce a durable authoritative head.");
    Environment.ExitCode = 1;
    return;
}

var persistedHead = await coordinator.ReadHeadAsync(worldId);
var headMatches = string.Equals(
    persistedHead?.RevisionId,
    result.Head.RevisionId,
    StringComparison.OrdinalIgnoreCase);

Console.WriteLine($"Persisted head matches result: {headMatches}");
if (!headMatches)
{
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine();
Console.WriteLine(result.Status == CanonicalWorldStateCommitStatus.Unchanged
    ? "Canonical commit complete. The state was identical, so Steward created no new revision."
    : "Canonical commit complete. The new StateRevision became authoritative only after durable storage.");

static DateTime GetLastWriteTimeUtcSafe(string path)
{
    try
    {
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }
    catch (IOException)
    {
        return DateTime.MinValue;
    }
    catch (UnauthorizedAccessException)
    {
        return DateTime.MinValue;
    }
}

static bool IsProcessRunning(string processName)
{
    foreach (var process in Process.GetProcessesByName(processName))
    {
        using (process)
        {
            try
            {
                if (!process.HasExited)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // Process disappeared during inspection.
            }
        }
    }

    return false;
}
