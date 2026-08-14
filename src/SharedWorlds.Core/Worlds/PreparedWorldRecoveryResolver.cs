using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Storage;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Turns durable prepared-World identity back into a current runtime location. Managed locations are
/// derived from WorkspaceId and the configured SafeWorld workspace root. Native locations are
/// reconstructed by an adapter that explicitly supports native recovery. Legacy absolute paths are
/// accepted only while old recovery journals are being migrated.
/// </summary>
public sealed class PreparedWorldRecoveryResolver
{
    private readonly ManagedWorkspaceStorage _managedWorkspaces;

    public PreparedWorldRecoveryResolver(ManagedWorkspaceStorage managedWorkspaces)
    {
        ArgumentNullException.ThrowIfNull(managedWorkspaces);
        _managedWorkspaces = managedWorkspaces;
    }

    internal ManagedWorkspaceStorage ManagedWorkspaces => _managedWorkspaces;

    /// <summary>
    /// Resolves a current working directory without a game installation when durable identity makes
    /// that possible. Legacy journals return their historical path as an explicit compatibility case;
    /// managed journals derive a current path from WorkspaceId; native-game identity returns null
    /// because only the adapter plus current installation metadata can reconstruct it safely.
    /// </summary>
    public string? ResolveWorkingDirectoryWithoutInstallation(
        WorkspaceRecoveryRecord recovery,
        string adapterId)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        EnsureAdapter(recovery, adapterId);

        var location = recovery.RecoveryLocation;
        if (location is null)
        {
            // Compatibility only. New journals must never place current-machine location authority here.
            ArgumentException.ThrowIfNullOrWhiteSpace(recovery.WorkingDirectory);
            return Path.GetFullPath(recovery.WorkingDirectory);
        }

        location.Validate();
        return location.Kind switch
        {
            PreparedWorldRecoveryLocationKind.SafeWorldManaged =>
                _managedWorkspaces.GetWorkspaceDirectory(recovery.Id, adapterId),
            PreparedWorldRecoveryLocationKind.NativeGame => null,
            _ => throw new InvalidDataException(
                $"Unsupported prepared-World recovery location kind '{location.Kind}'.")
        };
    }

    public PreparedWorld Resolve(
        WorkspaceRecoveryRecord recovery,
        IGameAdapter adapter,
        GameInstallation installation,
        EnvironmentManifest environment,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(environment);

        EnsureAdapter(recovery, adapter.Id);

        if (!string.Equals(environment.AdapterId, adapter.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Recovery environment belongs to adapter '{environment.AdapterId}', not '{adapter.Id}'.");
        }

        var location = recovery.RecoveryLocation;
        if (location is null)
        {
            var legacyWorkingDirectory = ResolveWorkingDirectoryWithoutInstallation(
                recovery,
                adapter.Id)
                ?? throw new InvalidOperationException(
                    "Legacy prepared-World recovery unexpectedly required installation-based resolution.");
            return new PreparedWorld(
                installation,
                legacyWorkingDirectory,
                environment,
                displayName);
        }

        location.Validate();
        switch (location.Kind)
        {
            case PreparedWorldRecoveryLocationKind.SafeWorldManaged:
                return new PreparedWorld(
                    installation,
                    _managedWorkspaces.GetWorkspaceDirectory(recovery.Id, adapter.Id),
                    environment,
                    displayName,
                    location);

            case PreparedWorldRecoveryLocationKind.NativeGame:
                if (adapter is not INativePreparedWorldRecoveryAdapter nativeAdapter)
                {
                    throw new InvalidOperationException(
                        $"Adapter '{adapter.Id}' has a native recovery location but does not implement native prepared-World recovery.");
                }

                var prepared = nativeAdapter.ResolveNativePreparedWorld(
                    installation,
                    environment,
                    location,
                    displayName);
                ArgumentNullException.ThrowIfNull(prepared);
                return prepared with { RecoveryLocation = location };

            default:
                throw new InvalidDataException(
                    $"Unsupported prepared-World recovery location kind '{location.Kind}'.");
        }
    }

    private static void EnsureAdapter(WorkspaceRecoveryRecord recovery, string adapterId)
    {
        if (!string.Equals(recovery.AdapterId, adapterId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Recovery workspace '{recovery.Id}' belongs to adapter '{recovery.AdapterId}', not '{adapterId}'.");
        }
    }
}
