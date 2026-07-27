using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio;

public sealed partial class FactorioAdapter : IPublicWorldExportAdapter
{
    private const int PublicExportEnvironmentSchemaVersion = 1;

    public Task<PublicWorldExportReadiness> CheckPublicWorldExportAsync(
        EnvironmentManifest exactEnvironment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exactEnvironment);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(exactEnvironment.AdapterId, Id, StringComparison.Ordinal))
        {
            return Task.FromResult(PublicWorldExportReadiness.Unsupported(
                $"Environment belongs to adapter '{exactEnvironment.AdapterId}', not Factorio."));
        }

        if (exactEnvironment.SchemaVersion != PublicExportEnvironmentSchemaVersion)
        {
            return Task.FromResult(PublicWorldExportReadiness.Unsupported(
                $"Factorio environment schema {exactEnvironment.SchemaVersion} has not been qualified for public World export."));
        }

        // Current Factorio capture authority is exactly one native save ZIP. The environment manifest
        // describes the game/mod setup but does not place mod binaries, player-data.json, credentials,
        // logs, config.ini, or other surrounding user-data files into the captured state package.
        return Task.FromResult(PublicWorldExportReadiness.Supported());
    }
}
