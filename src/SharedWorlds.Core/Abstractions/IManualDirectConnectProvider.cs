namespace SharedWorlds.Core.Abstractions;

/// <summary>
/// Optional adapter contract for a game whose native client can consume the published host address/port
/// directly, but for which Steward does not have a validated automatic client-launch path. This contract
/// is presentation-only: it does not create a prepared manual-session lifecycle, launch the game, observe
/// client completion, or own cleanup.
/// </summary>
public interface IManualDirectConnectProvider
{
    ManualDirectConnectInstruction GetManualDirectConnectInstruction(HostConnection host);
}

/// <summary>
/// User-visible native direct-connect guidance for an already-validated ready host.
/// Endpoint is kept separate so UI can present/copy it without parsing prose.
/// </summary>
public sealed record ManualDirectConnectInstruction(
    string Endpoint,
    string Instruction);
