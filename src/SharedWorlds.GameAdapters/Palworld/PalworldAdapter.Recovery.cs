using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Palworld;

public sealed partial class PalworldAdapter :
    INativePreparedWorldRecoveryAdapter,
    IPreparedWorldRecoveryPlanner
{
    private const string NativeRecoveryWorldIdKey = "worldId";
    private const string EnvironmentWorldIdKey = "dedicatedServerName";
    private const string EnvironmentHostingModeKey = "hostingMode";
    private const string DedicatedHostingMode = "dedicated-server";

    public Task<PreparedWorldRecoveryLocation> PlanPreparedWorldRecoveryAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        PreparedWorldPreparationContext preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(requiredEnvironment);
        ArgumentNullException.ThrowIfNull(preparation);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(CreateNativeRecoveryLocation(requiredEnvironment));
    }

    public async Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        PreparedWorldPreparationContext preparation,
        CancellationToken cancellationToken = default)
    {
        var recoveryLocation = await PlanPreparedWorldRecoveryAsync(
            installation,
            requiredEnvironment,
            preparation,
            cancellationToken);
        var prepared = await PrepareEnvironmentAsync(
            installation,
            requiredEnvironment,
            cancellationToken);
        return prepared with { RecoveryLocation = recoveryLocation };
    }

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

    private static PreparedWorldRecoveryLocation CreateNativeRecoveryLocation(
        EnvironmentManifest environment)
    {
        if (!string.Equals(environment.AdapterId, "palworld", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Environment belongs to adapter '{environment.AdapterId}', not Palworld.",
                nameof(environment));
        }

        if (environment.Configuration.TryGetValue(
                EnvironmentHostingModeKey,
                out var hostingMode) &&
            !string.Equals(hostingMode, DedicatedHostingMode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Palworld environment hosting mode '{hostingMode}' is not supported by the dedicated-host recovery path.");
        }

        if (!environment.Configuration.TryGetValue(
                EnvironmentWorldIdKey,
                out var worldId) ||
            string.IsNullOrWhiteSpace(worldId))
        {
            throw new InvalidDataException(
                $"Palworld environment is missing stable '{EnvironmentWorldIdKey}' recovery identity.");
        }

        RequireSafeWorldId(worldId);
        return PreparedWorldRecoveryLocation.Native(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NativeRecoveryWorldIdKey] = worldId
            });
    }

    private static void RequireSafeWorldId(string worldId)
    {
        if (string.Equals(worldId, ".", StringComparison.Ordinal) ||
            string.Equals(worldId, "..", StringComparison.Ordinal) ||
            Path.IsPathRooted(worldId) ||
            !string.Equals(Path.GetFileName(worldId), worldId, StringComparison.Ordinal) ||
            worldId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException(
                $"Palworld recovery world id is not a safe native directory identity: '{worldId}'.");
        }
    }
}
