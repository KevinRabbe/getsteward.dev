using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld;

/// <summary>
/// Owns one Palworld managed host session from temporary runtime-input installation through the
/// empirically proven post-exit restoration boundary. WorldOption.sav is read-only input: its exact
/// canonical file is parked for the session and is never rewritten or re-encoded.
/// </summary>
internal sealed class PalworldManagedHostSession : IDisposable
{
    private const string LauncherProcessName = "PalServer";
    private const string ShippingProcessName = "PalServer-Win64-Shipping-Cmd";
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(60);

    private readonly object _stopGate = new();
    private readonly SemaphoreSlim _restoreGate = new(1, 1);
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PalworldManagedHostLaunchContext _context;
    private readonly string _worldOptionPath;
    private readonly string _parkingPath;
    private readonly string _iniPath;
    private readonly byte[] _originalWorldOptionBytes;
    private readonly byte[] _originalIniBytes;
    private readonly string _originalWorldOptionHash;
    private readonly string _originalIniHash;
    private readonly string _transientPassword;
    private readonly int _restPort;
    private readonly byte[] _temporaryIniBytes;

    private Process? _launcher;
    private HttpClient? _httpClient;
    private PalworldRestApiClient? _restClient;
    private Task? _stopTask;
    private Task? _exitMonitorTask;
    private bool _worldOptionParked;
    private bool _temporaryIniInstalled;
    private bool _runtimeInputsRestored;
    private bool _disposed;

    private PalworldManagedHostSession(
        PalworldManagedHostLaunchContext context,
        string worldOptionPath,
        string parkingPath,
        string iniPath,
        byte[] originalWorldOptionBytes,
        byte[] originalIniBytes,
        string transientPassword,
        int restPort,
        byte[] temporaryIniBytes)
    {
        _context = context;
        _worldOptionPath = worldOptionPath;
        _parkingPath = parkingPath;
        _iniPath = iniPath;
        _originalWorldOptionBytes = originalWorldOptionBytes;
        _originalIniBytes = originalIniBytes;
        _originalWorldOptionHash = Sha256(originalWorldOptionBytes);
        _originalIniHash = Sha256(originalIniBytes);
        _transientPassword = transientPassword;
        _restPort = restPort;
        _temporaryIniBytes = temporaryIniBytes;
    }

    public GameSessionHandle Handle
        => _launcher is null
            ? throw new InvalidOperationException("Palworld managed host has not started.")
            : new GameSessionHandle(_launcher.Id, _launcher.StartTime.ToUniversalTime());

    public static async Task<PalworldManagedHostSession> StartAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoPalworldProcessRunning();

        var context = PalworldDedicatedServerHosting.PrepareManagedHostLaunch(world);
        var worldOptionPath = Path.Combine(context.WorldPath, "WorldOption.sav");
        var iniPath = Path.Combine(
            context.ServerRoot,
            "Pal",
            "Saved",
            "Config",
            "WindowsServer",
            "PalWorldSettings.ini");
        if (!File.Exists(worldOptionPath))
        {
            throw new InvalidOperationException(
                "This Palworld World does not contain WorldOption.sav. The proven managed runtime-input path currently requires that canonical startup input.");
        }

        if (!File.Exists(iniPath))
        {
            throw new InvalidOperationException(
                "PalWorldSettings.ini does not exist. Start and stop the dedicated server once before Steward manages this World.");
        }

        var originalWorldOptionBytes = await File.ReadAllBytesAsync(worldOptionPath, cancellationToken);
        var originalIniBytes = await File.ReadAllBytesAsync(iniPath, cancellationToken);
        PalworldWorldOptionSettingsSnapshot snapshot;
        if (RequiresOodle(originalWorldOptionBytes))
        {
            using var oodle = PalworldOodleCodec.LoadInstalledFromPalworldRoots(
                context.ServerRoot,
                world.Installation.RootPath);
            snapshot = PalworldWorldOptionSettingsReader.Read(originalWorldOptionBytes, oodle);
        }
        else
        {
            snapshot = PalworldWorldOptionSettingsReader.Read(originalWorldOptionBytes);
        }

        var restPort = ReadRequiredRestPort(snapshot);
        var transientPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var mirror = PalworldWorldOptionIniMirror.Create(snapshot, transientPassword, restPort);
        var temporaryIniBytes = Encoding.UTF8.GetBytes(mirror.Contents);
        var parkingPath = worldOptionPath + $".sharedworlds-managed-{Guid.NewGuid():N}";
        var session = new PalworldManagedHostSession(
            context,
            worldOptionPath,
            parkingPath,
            iniPath,
            originalWorldOptionBytes,
            originalIniBytes,
            transientPassword,
            restPort,
            temporaryIniBytes);

        try
        {
            await session.StartCoreAsync(snapshot, mirror.ManagementOverrides, cancellationToken);
            return session;
        }
        catch
        {
            await session.CleanupFailedStartAsync();
            session.Dispose();
            throw;
        }
    }

    public async Task RequestStopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        Task stopTask;
        lock (_stopGate)
        {
            _stopTask ??= StopCoreAsync();
            stopTask = _stopTask;
        }

        try
        {
            // Once the adapter accepts a safe-stop request, the actual save/shutdown/restoration
            // sequence is intentionally non-cancellable. Cancellation only stops this caller waiting.
            await stopTask.WaitAsync(cancellationToken);
        }
        catch
        {
            if (stopTask.IsFaulted && !_completion.Task.IsCompleted && IsAnyPalworldProcessRunning())
            {
                lock (_stopGate)
                {
                    if (ReferenceEquals(_stopTask, stopTask))
                    {
                        _stopTask = null;
                    }
                }
            }

            throw;
        }
    }

    public Task WaitForCompletionAsync()
    {
        ThrowIfDisposed();
        // A started Palworld host remains Steward responsibility until the adapter reaches a proven
        // end boundary. Caller cancellation must not release Core's reservation while PalServer lives.
        return _completion.Task;
    }

    private async Task StartCoreAsync(
        PalworldWorldOptionSettingsSnapshot snapshot,
        IReadOnlySet<string> managementOverrides,
        CancellationToken cancellationToken)
    {
        await WriteAtomicallyAsync(_iniPath, _temporaryIniBytes, cancellationToken);
        _temporaryIniInstalled = true;

        var mirroredConfig = PalworldRestAcceptanceConfigurationReader.Read(_context.ServerRoot);
        if (!mirroredConfig.ConfigExists ||
            !mirroredConfig.RestEnabled ||
            mirroredConfig.RestPort != _restPort ||
            !mirroredConfig.AdminPasswordConfigured ||
            !string.Equals(mirroredConfig.SelectedWorldId, _context.WorldId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Generated PalWorldSettings.ini did not pass Steward's own Palworld configuration preflight.");
        }

        var loadedPassword = PalworldRestAcceptanceConfigurationReader.ReadAdminPassword(_context.ServerRoot);
        if (!string.Equals(loadedPassword, _transientPassword, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Generated PalWorldSettings.ini did not round-trip Steward's transient AdminPassword.");
        }

        File.Move(_worldOptionPath, _parkingPath);
        _worldOptionParked = true;
        if (File.Exists(_worldOptionPath))
        {
            throw new IOException("WorldOption.sav still exists after Steward parked the canonical file.");
        }

        _launcher = PalworldDedicatedServerHosting.StartManagedHost(_context);
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_restPort}/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(5)
        };
        _restClient = new PalworldRestApiClient(_httpClient, "admin", _transientPassword);

        var info = await WaitForReadinessAsync(cancellationToken);
        if (!string.Equals(info.WorldGuid, _context.WorldId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Palworld REST reported World '{info.WorldGuid}', not Steward's selected World '{_context.WorldId}'.");
        }

        using var settings = await _restClient.GetSettingsAsync(cancellationToken);
        var verification = PalworldRuntimeSettingsVerifier.Verify(
            snapshot,
            settings.RootElement,
            _restPort,
            managementOverrides);
        if (!verification.IsMatch)
        {
            var detail = verification.Mismatches.Count > 0
                ? $" mismatches: {string.Join(", ", verification.Mismatches)}."
                : string.Empty;
            var unexposed = verification.UnexposedWorldSettings.Count > 0
                ? $" unexposed: {string.Join(", ", verification.UnexposedWorldSettings)}."
                : string.Empty;
            throw new InvalidDataException(
                $"Palworld's effective runtime settings do not match the canonical WorldOption mirror.{detail}{unexposed}");
        }

        _exitMonitorTask = MonitorUnexpectedExitAsync();
    }

    private async Task StopCoreAsync()
    {
        ThrowIfDisposed();
        var restClient = _restClient
            ?? throw new InvalidOperationException("Palworld REST control is unavailable for this managed host.");

        try
        {
            await restClient.SaveAsync(CancellationToken.None);
            await restClient.ShutdownAsync(
                1,
                "Steward is saving and stopping this World.",
                CancellationToken.None);

            if (!await WaitForPalworldExitAsync(ShutdownTimeout, CancellationToken.None))
            {
                throw new TimeoutException(
                    "Palworld did not fully exit after Steward's graceful shutdown request.");
            }

            await RestoreRuntimeInputsAfterExitAsync();
            _completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            if (!IsAnyPalworldProcessRunning())
            {
                var finalFailure = await TryRestoreAfterFailureAsync(exception);
                _completion.TrySetException(finalFailure);
                throw finalFailure;
            }

            throw;
        }
    }

    private async Task MonitorUnexpectedExitAsync()
    {
        try
        {
            await WaitForPalworldExitAsync(Timeout.InfiniteTimeSpan, CancellationToken.None);
            if (_completion.Task.IsCompleted)
            {
                return;
            }

            Task? stopTask;
            lock (_stopGate)
            {
                stopTask = _stopTask;
            }

            if (stopTask is not null)
            {
                try
                {
                    await stopTask;
                }
                catch
                {
                    // StopCoreAsync owns the authoritative failure and restoration path.
                }

                if (_completion.Task.IsCompleted)
                {
                    return;
                }
            }

            await RestoreRuntimeInputsAfterExitAsync();
            _completion.TrySetException(new InvalidOperationException(
                "Palworld ended outside Steward's proven managed /save -> /shutdown boundary. Canonical runtime inputs were restored, but Steward will preserve the workspace for recovery instead of committing this session automatically."));
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }

    private async Task<PalworldServerInfo> WaitForReadinessAsync(CancellationToken cancellationToken)
    {
        var restClient = _restClient
            ?? throw new InvalidOperationException("Palworld REST control was not initialized.");
        var deadline = DateTimeOffset.UtcNow + ReadinessTimeout;
        Exception? lastFailure = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_launcher is not null && _launcher.HasExited && !IsAnyPalworldProcessRunning())
            {
                throw new InvalidOperationException(
                    "Palworld process tree exited before its authenticated REST endpoint became ready.",
                    lastFailure);
            }

            try
            {
                return await restClient.GetInfoAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or
                InvalidOperationException or
                InvalidDataException or
                TaskCanceledException)
            {
                lastFailure = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"Palworld did not expose authenticated REST readiness within {ReadinessTimeout.TotalSeconds:0} seconds.",
            lastFailure);
    }

    private async Task<Exception> TryRestoreAfterFailureAsync(Exception originalFailure)
    {
        try
        {
            await RestoreRuntimeInputsAfterExitAsync();
            return originalFailure;
        }
        catch (Exception restorationFailure)
        {
            return new AggregateException(
                "Palworld managed stop failed and Steward also could not restore canonical runtime inputs cleanly.",
                originalFailure,
                restorationFailure);
        }
    }

    private async Task RestoreRuntimeInputsAfterExitAsync()
    {
        await _restoreGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_runtimeInputsRestored)
            {
                return;
            }

            if (IsAnyPalworldProcessRunning())
            {
                throw new InvalidOperationException(
                    "Steward refused to restore canonical Palworld runtime inputs while a Palworld server process is still running.");
            }

            string? unexpectedWorldOptionPath = null;
            if (File.Exists(_worldOptionPath))
            {
                unexpectedWorldOptionPath = _worldOptionPath + $".sharedworlds-unexpected-{Guid.NewGuid():N}";
                File.Move(_worldOptionPath, unexpectedWorldOptionPath);
            }

            var parkingMissing = !File.Exists(_parkingPath);
            if (!parkingMissing)
            {
                File.Move(_parkingPath, _worldOptionPath);
                _worldOptionParked = false;
            }
            else
            {
                // Restore availability first, then fail the invariant. The in-memory bytes are the
                // exact bytes read before the session, but a missing parked file is still abnormal.
                await WriteAtomicallyAsync(_worldOptionPath, _originalWorldOptionBytes, CancellationToken.None);
                _worldOptionParked = false;
            }

            await WriteAtomicallyAsync(_iniPath, _originalIniBytes, CancellationToken.None);
            _temporaryIniInstalled = false;

            var worldOptionHash = await Sha256FileAsync(_worldOptionPath);
            var iniHash = await Sha256FileAsync(_iniPath);
            if (!string.Equals(worldOptionHash, _originalWorldOptionHash, StringComparison.Ordinal) ||
                !string.Equals(iniHash, _originalIniHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Palworld canonical runtime inputs were not restored byte-for-byte after process exit.");
            }

            var credentialBytes = Encoding.UTF8.GetBytes(_transientPassword);
            try
            {
                if (ContainsSequence(await File.ReadAllBytesAsync(_worldOptionPath), credentialBytes) ||
                    ContainsSequence(await File.ReadAllBytesAsync(_iniPath), credentialBytes))
                {
                    throw new InvalidDataException(
                        "Steward's transient Palworld credential remained in a restored canonical runtime input.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(credentialBytes);
            }

            _runtimeInputsRestored = true;

            if (unexpectedWorldOptionPath is not null)
            {
                throw new InvalidDataException(
                    $"Palworld unexpectedly generated WorldOption.sav while Steward's canonical file was parked. Steward preserved that unexpected file for recovery at '{unexpectedWorldOptionPath}' and restored the canonical original, but this session is not safe for automatic commit.");
            }

            if (parkingMissing)
            {
                throw new InvalidDataException(
                    "Steward's parked canonical WorldOption.sav disappeared during the managed session. The exact pre-session bytes were restored from memory, but this session is not safe for automatic commit.");
            }
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    private async Task CleanupFailedStartAsync()
    {
        try
        {
            if (IsAnyPalworldProcessRunning())
            {
                if (_restClient is not null)
                {
                    try
                    {
                        await _restClient.ShutdownAsync(
                            0,
                            "Steward aborted Palworld startup before the managed session became ready.",
                            CancellationToken.None);
                    }
                    catch
                    {
                        // Readiness may have failed before REST became usable. Forced cleanup below is
                        // allowed here because Core has not accepted this as a started user session.
                    }
                }

                if (!await WaitForPalworldExitAsync(TimeSpan.FromSeconds(10), CancellationToken.None))
                {
                    await ForceCleanupPalworldProcessesAsync();
                }
            }

            if (!IsAnyPalworldProcessRunning() && (_worldOptionParked || _temporaryIniInstalled))
            {
                await RestoreRuntimeInputsAfterExitAsync();
            }
        }
        catch
        {
            // The original startup exception remains primary. The adapter's normal finalization and
            // recovery path will still see any remaining files; never mask the launch failure here.
        }
    }

    private async Task<bool> WaitForPalworldExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var infinite = timeout == Timeout.InfiniteTimeSpan;
        var deadline = infinite ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + timeout;
        while (infinite || DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var launcherExited = true;
            if (_launcher is not null)
            {
                try
                {
                    launcherExited = _launcher.HasExited;
                }
                catch (InvalidOperationException)
                {
                    launcherExited = true;
                }
            }

            if (launcherExited && !IsAnyPalworldProcessRunning())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return false;
    }

    private static async Task ForceCleanupPalworldProcessesAsync()
    {
        foreach (var processName in new[] { ShippingProcessName, LauncherProcessName })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync();
                        }
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException or
                        System.ComponentModel.Win32Exception)
                    {
                        // Final process observation and runtime-input restoration decide safety.
                    }
                }
            }
        }
    }

    private static void EnsureNoPalworldProcessRunning()
    {
        if (IsAnyPalworldProcessRunning())
        {
            throw new InvalidOperationException(
                "A Palworld dedicated-server process is already running. Steward will not replace runtime inputs underneath an existing server.");
        }
    }

    private static bool IsAnyPalworldProcessRunning()
        => IsProcessRunning(LauncherProcessName) || IsProcessRunning(ShippingProcessName);

    private static bool IsProcessRunning(string processName)
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

    private static bool RequiresOodle(ReadOnlySpan<byte> save)
        => save.Length >= 12 && save.Slice(8, 3).SequenceEqual("PlM"u8);

    private static int ReadRequiredRestPort(PalworldWorldOptionSettingsSnapshot snapshot)
    {
        var matches = snapshot.Settings
            .Where(setting => string.Equals(setting.Name, "RESTAPIPort", StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1 ||
            matches[0].Value is null ||
            !int.TryParse(matches[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            throw new InvalidDataException(
                "Canonical WorldOption.sav does not contain one valid RESTAPIPort that Steward can preserve for the managed session.");
        }

        return port;
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Palworld runtime-input path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.sharedworlds-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static string Sha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private static async Task<string> Sha256FileAsync(string path)
        => Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        => !needle.IsEmpty && haystack.IndexOf(needle) >= 0;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient?.Dispose();
        _launcher?.Dispose();
        _restoreGate.Dispose();
        CryptographicOperations.ZeroMemory(_originalWorldOptionBytes);
        CryptographicOperations.ZeroMemory(_originalIniBytes);
        CryptographicOperations.ZeroMemory(_temporaryIniBytes);
        _disposed = true;
    }
}
