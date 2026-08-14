using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.GameAdapters.Factorio;

public sealed partial class FactorioAdapter : IPreparedWorldRecoveryPlanner
{
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

        if (!string.Equals(requiredEnvironment.AdapterId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Environment belongs to adapter '{requiredEnvironment.AdapterId}', not Factorio.",
                nameof(requiredEnvironment));
        }

        return Task.FromResult(PreparedWorldRecoveryLocation.Managed());
    }

    public async Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        PreparedWorldPreparationContext preparation,
        CancellationToken cancellationToken = default)
    {
        _ = await PlanPreparedWorldRecoveryAsync(
            installation,
            requiredEnvironment,
            preparation,
            cancellationToken);

        var installedVersion = await FactorioEnvironmentInspector.ReadInstalledGameVersionAsync(
            installation,
            cancellationToken);
        if (!string.Equals(installedVersion, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            throw new EnvironmentReproductionException(
                Id,
                $"installed game version '{installedVersion}' does not match required version '{requiredEnvironment.GameVersion}'.");
        }

        var prepared = await FactorioWorldOperations.PrepareManagedEnvironmentAsync(
            installation,
            requiredEnvironment,
            preparation.ManagedWorkingDirectory,
            cancellationToken);
        return prepared with
        {
            RecoveryLocation = PreparedWorldRecoveryLocation.Managed()
        };
    }
}
