namespace SharedWorlds.Core.Abstractions;

/// <summary>
/// Optional adapter contract for a managed host that can expose the game-owned connection material
/// needed by another Steward client after the host process is actually ready. The adapter deliberately
/// does not decide the network-visible address; session coordination owns that deployment/network fact.
/// </summary>
public interface IManagedHostEndpointProvider
{
    ManagedHostEndpoint? GetManagedHostEndpoint(GameSessionHandle session);
}

/// <summary>
/// Adapter-owned connection material for an already-running managed host. Port and token are enough
/// for coordinators that can derive the network-visible address from the authenticated host connection.
/// </summary>
public sealed record ManagedHostEndpoint(
    int? Port = null,
    string? JoinToken = null);
