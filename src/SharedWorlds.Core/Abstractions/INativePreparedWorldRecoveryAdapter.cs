using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Core.Abstractions;

/// <summary>
/// Optional adapter capability for reconstructing a game-owned prepared World from stable native
/// identity. Implementations must not treat an absolute path stored in the recovery journal as
/// identity; the current installation/environment plus RecoveryLocation.NativeIdentity are the input.
/// </summary>
public interface INativePreparedWorldRecoveryAdapter
{
    PreparedWorld ResolveNativePreparedWorld(
        GameInstallation installation,
        EnvironmentManifest environment,
        PreparedWorldRecoveryLocation recoveryLocation,
        string? displayName = null);
}
