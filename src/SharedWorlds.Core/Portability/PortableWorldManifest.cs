using SharedWorlds.Core.Environment;

namespace SharedWorlds.Core.Portability;

/// <summary>
/// Stable, game-agnostic metadata for one immutable portable World snapshot.
/// The payload remains opaque adapter-owned bytes; this contract only describes the snapshot,
/// its exact environment, integrity, and optional human-facing provenance.
/// </summary>
public sealed record PortableWorldManifest(
    int FormatVersion,
    string GameAdapterId,
    string WorldName,
    string SnapshotId,
    DateTimeOffset CreatedAt,
    EnvironmentManifest Environment,
    PortableWorldPayload Payload,
    PortableWorldPresentation? Presentation = null,
    PortableWorldOrigin? StartedFrom = null);

/// <summary>
/// Integrity metadata for the single opaque World-state payload in format V1.
/// </summary>
public sealed record PortableWorldPayload(
    string EntryName,
    long Length,
    string Sha256);

/// <summary>
/// Optional creator-facing metadata. None of these fields participate in World identity.
/// </summary>
public sealed record PortableWorldPresentation(
    string? Creator = null,
    string? Description = null,
    string? SourceUrl = null);

/// <summary>
/// Lightweight provenance for an independent World created from another published snapshot.
/// This is attribution only: there is no merge or synchronization relationship.
/// </summary>
public sealed record PortableWorldOrigin(
    string SnapshotId,
    string? WorldName = null,
    string? Creator = null);
