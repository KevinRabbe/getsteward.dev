using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Enforces the two-phase prepared-World contract: an adapter declares durable recovery identity
/// before materialization, then the materialized result must match that exact plan. This prevents a
/// prepared runtime from silently changing ownership/location semantics after Core has journaled it.
/// </summary>
public static class PreparedWorldRecoveryPlanPolicy
{
    public static void ValidatePlan(
        string adapterId,
        PreparedWorldPreparationContext preparation,
        PreparedWorldRecoveryLocation plannedLocation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(plannedLocation);
        ArgumentException.ThrowIfNullOrWhiteSpace(preparation.ManagedWorkingDirectory);

        plannedLocation.Validate();
    }

    public static PreparedWorldRecoveryClassification ValidateMaterializedResult(
        string adapterId,
        PreparedWorldPreparationContext preparation,
        PreparedWorldRecoveryLocation plannedLocation,
        PreparedWorld preparedWorld)
    {
        ValidatePlan(adapterId, preparation, plannedLocation);
        ArgumentNullException.ThrowIfNull(preparedWorld);

        var materializedLocation = preparedWorld.RecoveryLocation
            ?? throw new InvalidOperationException(
                $"Adapter '{adapterId}' planned stable prepared-World recovery identity but returned a materialized World without that identity.");
        materializedLocation.Validate();

        if (!LocationsEqual(plannedLocation, materializedLocation))
        {
            throw new InvalidOperationException(
                $"Adapter '{adapterId}' changed prepared-World recovery identity during materialization. Safe World will preserve the preflight journal and refuse to launch or restore state.");
        }

        return PreparedWorldRecoveryPolicy.Classify(
            adapterId,
            preparation,
            preparedWorld);
    }

    internal static bool LocationsEqual(
        PreparedWorldRecoveryLocation left,
        PreparedWorldRecoveryLocation right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        left.Validate();
        right.Validate();

        if (left.SchemaVersion != right.SchemaVersion || left.Kind != right.Kind)
        {
            return false;
        }

        if (left.Kind == PreparedWorldRecoveryLocationKind.SafeWorldManaged)
        {
            return true;
        }

        var leftIdentity = left.NativeIdentity;
        var rightIdentity = right.NativeIdentity;
        if (leftIdentity is null || rightIdentity is null || leftIdentity.Count != rightIdentity.Count)
        {
            return false;
        }

        foreach (var pair in leftIdentity)
        {
            if (!rightIdentity.TryGetValue(pair.Key, out var value) ||
                !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
