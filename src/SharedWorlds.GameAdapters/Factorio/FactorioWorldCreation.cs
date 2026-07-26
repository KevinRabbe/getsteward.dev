using System.Diagnostics;
using System.Globalization;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

internal static partial class FactorioWorldOperations
{
    internal const string CreationPresetSetting = "preset";
    internal const string CreationSeedSetting = "seed";

    private const string CreationRuntimeDirectoryName = "create-runtime";
    private const string SteamAppIdFileName = "steam_appid.txt";
    private const string FactorioSteamAppId = "427520";

    public static async Task<NativeWorldCreationResult> CreateNativeWorldAsync(
        GameInstallation installation,
        WorldCreationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DisplayName);
        cancellationToken.ThrowIfCancellationRequested();

        var environment = await FactorioEnvironmentInspector.InspectAsync(
            installation,
            cancellationToken);
        var prepared = await PrepareEnvironmentAsync(
            installation,
            environment,
            cancellationToken);

        try
        {
            await GenerateNativeSaveAsync(prepared, request, cancellationToken);
            var captured = await CaptureStateAsync(prepared, cancellationToken);
            return new NativeWorldCreationResult(environment, captured);
        }
        finally
        {
            // Native creation is not a playable session and has no recovery responsibility. Once
            // the generated save has been copied into a disposable CapturedState package, the
            // adapter-owned generation workspace is no longer needed.
            TryDeleteDirectory(prepared.WorkingDirectory);
        }
    }

    internal static IReadOnlyList<string> BuildNativeCreateArguments(
        string savePath,
        WorldCreationRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savePath);
        ArgumentNullException.ThrowIfNull(request);

        string? preset = null;
        uint? seed = null;
        var settings = request.Settings ?? new Dictionary<string, string>();

        foreach (var (key, rawValue) in settings)
        {
            if (string.Equals(key, CreationPresetSetting, StringComparison.OrdinalIgnoreCase))
            {
                if (preset is not null)
                {
                    throw new ArgumentException(
                        "Factorio World creation contains the preset setting more than once.",
                        nameof(request));
                }

                preset = ValidatePreset(rawValue);
                continue;
            }

            if (string.Equals(key, CreationSeedSetting, StringComparison.OrdinalIgnoreCase))
            {
                if (seed is not null)
                {
                    throw new ArgumentException(
                        "Factorio World creation contains the seed setting more than once.",
                        nameof(request));
                }

                if (!uint.TryParse(
                        rawValue,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parsedSeed))
                {
                    throw new ArgumentException(
                        "Factorio World creation seed must be an unsigned 32-bit integer.",
                        nameof(request));
                }

                seed = parsedSeed;
                continue;
            }

            throw new ArgumentException(
                $"Factorio World creation setting '{key}' is not supported by this Steward build.",
                nameof(request));
        }

        var arguments = new List<string>
        {
            "--create",
            Path.GetFullPath(savePath)
        };

        if (preset is not null)
        {
            arguments.Add("--preset");
            arguments.Add(preset);
        }

        if (seed is not null)
        {
            arguments.Add("--map-gen-seed");
            arguments.Add(seed.Value.ToString(CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    private static async Task GenerateNativeSaveAsync(
        PreparedWorld world,
        WorldCreationRequest request,
        CancellationToken cancellationToken)
    {
        FactorioWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        var savePath = GetPreparedSavePath(world);
        var configPath = GetWorkspaceConfigPath(world);
        var modDirectory = GetWorkspaceModsDirectory(world);
        var runtimeDirectory = Path.Combine(
            world.WorkingDirectory,
            CreationRuntimeDirectoryName);

        FactorioWorkspaceOwnership.RequireOwnedPath(
            world.WorkingDirectory,
            savePath,
            "native creation save");
        FactorioWorkspaceOwnership.RequireOwnedPath(
            world.WorkingDirectory,
            configPath,
            "native creation config");
        FactorioWorkspaceOwnership.RequireOwnedPath(
            world.WorkingDirectory,
            modDirectory,
            "native creation mod directory");
        FactorioWorkspaceOwnership.RequireOwnedPath(
            world.WorkingDirectory,
            runtimeDirectory,
            "native creation runtime directory");

        if (File.Exists(savePath))
        {
            throw new IOException(
                $"Factorio native World creation destination already exists: '{savePath}'.");
        }

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                "The isolated Factorio creation config does not exist.",
                configPath);
        }

        if (!Directory.Exists(modDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The isolated Factorio creation mod directory does not exist: '{modDirectory}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        Directory.CreateDirectory(runtimeDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(runtimeDirectory, SteamAppIdFileName),
            FactorioSteamAppId,
            cancellationToken);

        var executable = GetExecutablePath(world.Installation);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = runtimeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("--mod-directory");
        startInfo.ArgumentList.Add(modDirectory);
        foreach (var argument in BuildNativeCreateArguments(savePath, request))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Failed to start Factorio native World creation through '{executable}'.");

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Factorio native World creation exited with code {process.ExitCode}.");
        }

        RequireGeneratedSave(savePath, world.WorkingDirectory);
    }

    private static string ValidatePreset(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            value.Any(char.IsControl) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Factorio World creation preset must be 1-128 non-control characters without leading or trailing whitespace.",
                nameof(value));
        }

        return value;
    }

    private static void RequireGeneratedSave(string savePath, string workspace)
    {
        if (!File.Exists(savePath))
        {
            throw new FileNotFoundException(
                "Factorio reported successful native World creation but did not create the expected save.",
                savePath);
        }

        FactorioWorkspaceOwnership.RequireOwnedPath(
            workspace,
            savePath,
            "generated save");

        var info = new FileInfo(savePath);
        if (info.Length <= 0)
        {
            throw new InvalidDataException(
                "Factorio native World creation produced an empty save file.");
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Factorio native World creation produced a linked/reparse save file.");
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
            // Cancellation remains authoritative. Cleanup/finalization still removes the owned
            // workspace after the process has either exited or can no longer be controlled here.
        }
    }
}
