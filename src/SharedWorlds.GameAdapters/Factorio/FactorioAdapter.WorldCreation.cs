using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

public sealed partial class FactorioAdapter
{
    public Task<NativeWorldCreationResult> CreateWorldAsync(
        GameInstallation installation,
        WorldCreationRequest request,
        CancellationToken cancellationToken = default)
        => FactorioWorldOperations.CreateNativeWorldAsync(
            installation,
            request,
            cancellationToken);
}
