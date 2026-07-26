using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

public sealed partial class FactorioAdapter
{
    // Keep the existing concrete capability property stable while making the new opt-in capability
    // visible through the product's IGameAdapter boundary used by catalogs/lifecycle code.
    GameAdapterCapabilities IGameAdapter.Capabilities =>
        Capabilities | GameAdapterCapabilities.NativeWorldCreation;

    public Task<NativeWorldCreationResult> CreateWorldAsync(
        GameInstallation installation,
        WorldCreationRequest request,
        CancellationToken cancellationToken = default)
        => FactorioWorldOperations.CreateNativeWorldAsync(
            installation,
            request,
            cancellationToken);
}
