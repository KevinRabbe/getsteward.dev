using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Enforces the durable-location contract at the Core/adapter boundary. Runtime absolute paths are
/// allowed, but a migrated adapter must either use the exact managed location offered by Core or
/// provide stable native-game identity. Null recovery locations are a temporary compatibility state
/// for adapters that still implement only the legacy preparation boundary.
/// </summary>
public static class PreparedWorldRecoveryPolicy
{
    public static PreparedWorldRecoveryClassification Classify(
        string adapterId,
        PreparedWorldPreparationContext preparation,
        PreparedWorld preparedWorld)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(preparedWorld);
        ArgumentException.ThrowIfNullOrWhiteSpace(preparation.ManagedWorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(preparedWorld.WorkingDirectory);

        var recoveryLocation = preparedWorld.RecoveryLocation;
        if (recoveryLocation is null)
        {
            return PreparedWorldRecoveryClassification.LegacyAbsolutePath;
        }

        recoveryLocation.Validate();
        if (recoveryLocation.Kind == PreparedWorldRecoveryLocationKind.NativeGame)
        {
            return PreparedWorldRecoveryClassification.NativeGame;
        }

        if (recoveryLocation.Kind != PreparedWorldRecoveryLocationKind.SafeWorldManaged)
        {
            throw new InvalidDataException(
                $"Adapter '{adapterId}' returned unsupported prepared-World recovery kind '{recoveryLocation.Kind}'.");
        }

        if (!PathsEqual(
                preparation.ManagedWorkingDirectory,
                preparedWorld.WorkingDirectory))
        {
            throw new InvalidOperationException(
                $"Adapter '{adapterId}' declared a SafeWorld-managed workspace but returned runtime path " +
                $"'{preparedWorld.WorkingDirectory}' instead of the identity-bound path offered by Core.");
        }

        return PreparedWorldRecoveryClassification.SafeWorldManaged;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}

public enum PreparedWorldRecoveryClassification
{
    LegacyAbsolutePath,
    SafeWorldManaged,
    NativeGame
}
