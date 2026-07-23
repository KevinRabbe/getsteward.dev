using System.Diagnostics;
using System.Security.Cryptography;
using SharedWorlds.GameAdapters.Palworld;

Console.WriteLine("SharedWorlds Palworld WorldOption settings probe (read-only)");
Console.WriteLine();

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("This acceptance probe currently requires Windows because the real PlM decoder path loads an Oodle runtime DLL.");
    Environment.ExitCode = 2;
    return;
}

var succeeded = await RunAsync();
Environment.ExitCode = succeeded ? 0 : 2;

static async Task<bool> RunAsync()
{
    if (IsAnyPalworldProcessRunning())
    {
        Console.Error.WriteLine("PalServer is running. Stop it before reading canonical WorldOption.sav.");
        return false;
    }

    var adapter = new PalworldAdapter();
    var installations = await adapter.DiscoverInstallationsAsync();
    var candidates = installations
        .Where(installation =>
            installation.Metadata is not null &&
            installation.Metadata.TryGetValue(PalworldInstallationDiscovery.DedicatedServerRootPathKey, out var root) &&
            !string.IsNullOrWhiteSpace(root))
        .ToArray();

    if (candidates.Length != 1)
    {
        Console.Error.WriteLine(
            $"Expected exactly one discovered Palworld installation with a dedicated server, found {candidates.Length}.");
        return false;
    }

    var installation = candidates[0];
    var serverRoot = installation.Metadata![PalworldInstallationDiscovery.DedicatedServerRootPathKey];
    var configuration = PalworldRestAcceptanceConfigurationReader.Read(serverRoot);
    if (string.IsNullOrWhiteSpace(configuration.SelectedWorldId))
    {
        Console.Error.WriteLine("DedicatedServerName was not found in GameUserSettings.ini.");
        return false;
    }

    var worlds = await adapter.DiscoverWorldsAsync(installation);
    var world = worlds.SingleOrDefault(candidate =>
        candidate.Id.StartsWith("dedicated:", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(candidate.SourcePath)),
            configuration.SelectedWorldId,
            StringComparison.OrdinalIgnoreCase));
    if (world is null)
    {
        Console.Error.WriteLine($"Selected dedicated World {configuration.SelectedWorldId} was not discovered.");
        return false;
    }

    var worldOptionPath = Path.Combine(world.SourcePath, "WorldOption.sav");
    if (!File.Exists(worldOptionPath))
    {
        Console.Error.WriteLine("Selected World does not contain WorldOption.sav.");
        return false;
    }

    byte[] originalBytes;
    try
    {
        originalBytes = await File.ReadAllBytesAsync(worldOptionPath);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"WorldOption.sav could not be read: {exception.Message}");
        return false;
    }

    var originalHash = Sha256(originalBytes);
    Console.WriteLine("worldOption:");
    Console.WriteLine($"  worldId: {configuration.SelectedWorldId}");
    Console.WriteLine($"  path: {worldOptionPath}");
    Console.WriteLine($"  container: {PalworldWorldOptionAdminPasswordRuntimeOverlay.DescribeContainer(originalBytes)}");
    Console.WriteLine($"  sha256Before: {originalHash}");

    PalworldOodleCodec? oodleCodec = null;
    try
    {
        if (PalworldWorldOptionAdminPasswordRuntimeOverlay.RequiresOodle(originalBytes))
        {
            try
            {
                oodleCodec = PalworldOodleCodec.LoadFromPalworldRoots(serverRoot, installation.RootPath);
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or
                DllNotFoundException or
                BadImageFormatException or
                EntryPointNotFoundException)
            {
                Console.Error.WriteLine($"Oodle decoder discovery failed: {exception.Message}");
                return false;
            }

            Console.WriteLine($"  oodleLibrary: {oodleCodec.LibraryPath}");
            Console.WriteLine("  oodleUsage: decode-only");
        }

        PalworldWorldOptionSettingsSnapshot snapshot;
        try
        {
            snapshot = PalworldWorldOptionSettingsReader.Read(originalBytes, oodleCodec);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            InvalidOperationException or
            ArgumentException or
            OverflowException)
        {
            Console.Error.WriteLine($"WorldOption settings extraction failed closed: {exception.Message}");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine("settings:");
        Console.WriteLine($"  container: {snapshot.Container}");
        Console.WriteLine($"  count: {snapshot.Settings.Count}");
        foreach (var setting in snapshot.Settings)
        {
            var valueType = setting.ValueType ?? "-";
            var value = setting.IsSensitive
                ? "<redacted>"
                : setting.Value ?? $"<opaque {setting.SerializedValueBytes} bytes>";
            Console.WriteLine($"  {setting.Name} | {setting.PropertyType} | {valueType} | {value}");
        }

        string finalHash;
        try
        {
            finalHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(worldOptionPath)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"WorldOption.sav could not be re-read for the final hash: {exception.Message}");
            return false;
        }

        var unchanged = string.Equals(originalHash, finalHash, StringComparison.Ordinal);
        Console.WriteLine();
        Console.WriteLine("result:");
        Console.WriteLine($"  sha256After: {finalHash}");
        Console.WriteLine($"  worldOptionUnchanged: {unchanged}");
        Console.WriteLine($"worldOptionSettingsReadOnlyExtracted: {unchanged && snapshot.Settings.Count > 0}");
        return unchanged && snapshot.Settings.Count > 0;
    }
    finally
    {
        oodleCodec?.Dispose();
        CryptographicOperations.ZeroMemory(originalBytes);
    }
}

static string Sha256(ReadOnlySpan<byte> bytes)
    => Convert.ToHexString(SHA256.HashData(bytes));

static bool IsAnyPalworldProcessRunning()
    => IsProcessRunning("PalServer") || IsProcessRunning("PalServer-Win64-Shipping-Cmd");

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
                // Process disappeared while being observed.
            }
        }
    }

    return false;
}
