using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Palworld;

public sealed partial class PalworldAdapter : INativePreparedWorldRecoveryAdapter
{
    private const string NativeRecoveryWorldIdKey = "worldId";

    public PreparedWorld ResolveNativePreparedWorld(
        GameInstallation installation,
        EnvironmentManifest environment,
        PreparedWorldRecoveryLocation recoveryLocation,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(recoveryLocation);
        recoveryLocation.Validate();

        if (recoveryLocation.Kind != PreparedWorldRecoveryLocationKind.NativeGame ||
            recoveryLocation.NativeIdentity is null ||
            !recoveryLocation.NativeIdentity.TryGetValue(
                NativeRecoveryWorldIdKey,
                out var expectedWorldId) ||
            string.IsNullOrWhiteSpace(expectedWorldId))
        {
            throw new InvalidDataException(
                "Palworld native recovery requires stable 'worldId' identity.");
        }

        // The existing validated dedicated-server preparation path reconstructs the current native
        // location from installation metadata + the environment's dedicated World id. Recovery never
        // trusts the journal's old absolute path.
        var prepared = PalworldDedicatedServerHosting.PrepareEnvironment(
            installation,
            environment);
        var actualWorldId = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(prepared.WorkingDirectory)));
        if (!string.Equals(actualWorldId, expectedWorldId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Palworld recovery identity '{expectedWorldId}' does not match environment World id '{actualWorldId}'.");
        }

        return prepared with
        {
            DisplayName = displayName,
            RecoveryLocation = recoveryLocation
        };
    }
}
