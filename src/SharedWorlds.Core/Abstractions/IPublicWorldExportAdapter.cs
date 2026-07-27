using SharedWorlds.Core.Environment;

namespace SharedWorlds.Core.Abstractions;

/// <summary>
/// Positive adapter-owned authority for publishing a captured World state to untrusted strangers.
///
/// Private capture/import support does not imply public-export safety. An adapter implements this
/// interface only after its canonical captured-state boundary has been proven safe to distribute for
/// at least one exact environment. The returned decision may still block environments (for example,
/// modded variants) whose captured bytes have not earned that claim.
///
/// Supported means the adapter's existing canonical state package may be placed into a Safe World
/// portable artifact as-is. It does not authorize redistributing the game, mods, credentials, player
/// identity state, or any other files outside that already-qualified captured-state package.
/// </summary>
public interface IPublicWorldExportAdapter
{
    Task<PublicWorldExportReadiness> CheckPublicWorldExportAsync(
        EnvironmentManifest exactEnvironment,
        CancellationToken cancellationToken = default);
}

public sealed record PublicWorldExportReadiness(
    bool IsSupported,
    string? Reason = null)
{
    public static PublicWorldExportReadiness Supported()
        => new(IsSupported: true);

    public static PublicWorldExportReadiness Unsupported(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(IsSupported: false, Reason: reason);
    }
}
