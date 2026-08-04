using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharedWorlds.BringHereProbe;

internal static class ProbeContract
{
    public const int EvidenceSchemaVersion = 1;
    public const int AcceptanceBuildSchemaVersion = 2;
    public const string EvidenceDocumentType = "safe-world.bring-here-two-pc-evidence";
    public const string AcceptanceBuildDocumentType = "steward.e4-desktop-acceptance-build";
    public const string SourceBeforeRole = "source-before";
    public const string TargetAfterRole = "target-after";
    public const string SourceAfterRole = "source-after";
    public const string DesktopProcessName = "SharedWorlds.Desktop";

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 48,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static JsonSerializerOptions CanonicalJsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        MaxDepth = 48
    };
}

internal sealed record AcceptanceBuildManifest(
    string DocumentType,
    int SchemaVersion,
    string CommitSha,
    string BuiltAtUtc,
    string Runtime,
    string Configuration,
    bool SelfContained,
    string Executable,
    string SteamNativeRuntime,
    IReadOnlyList<AcceptanceBuildFile>? Files);

internal sealed record AcceptanceBuildFile(
    string Path,
    long ByteSize,
    string Sha256);

internal sealed record VerifiedPackageFile(
    string Path,
    long ByteSize,
    string Sha256);

internal sealed record DeviceSettingsEnvelope(
    string DocumentType,
    int SchemaVersion,
    DeviceSettingsPayload? Payload);

internal sealed record DeviceSettingsPayload(
    bool AllowHosting,
    bool HostingPreferenceExplicit,
    string? InstallationId);

internal sealed record BuildEvidence(
    string CommitSha,
    string ManifestSha256,
    string PackageFingerprintSha256,
    string DesktopExecutableSha256);

internal sealed record WorldEvidence(
    string WorldId,
    string Name,
    string GameAdapterId,
    int SharingMode,
    string StateRevisionId,
    string EnvironmentRevisionId,
    string? StateParentRevisionId,
    string? EnvironmentParentRevisionId,
    string StatePackageId,
    string GameVersion,
    string StateRevisionSha256,
    string EnvironmentRevisionSha256,
    long PayloadByteSize,
    string PayloadSha256);

internal sealed record JournalEvidence(
    string InstallationId,
    string? DesiredStateRevisionId,
    string? DesiredEnvironmentRevisionId,
    string? ConfirmedStateRevisionId,
    string? ConfirmedEnvironmentRevisionId,
    string DesiredName,
    string DesiredGameAdapterId,
    string ConfirmedName,
    string ConfirmedGameAdapterId,
    bool IsSynchronized);

internal sealed record LocalSnapshotEvidence(
    string InstallationId,
    WorldEvidence World,
    JournalEvidence Journal);

internal sealed record BringHereAcceptanceEvidence(
    string DocumentType,
    int SchemaVersion,
    string Role,
    DateTimeOffset CollectedAtUtc,
    string MachineName,
    int DesktopProcessCount,
    string InstallationId,
    BuildEvidence Build,
    WorldEvidence World,
    JournalEvidence Journal,
    string? SourceEvidenceSha256,
    string? TargetEvidenceSha256);

internal sealed record StateRevisionFingerprint(
    string Id,
    string WorldId,
    string? ParentRevisionId,
    string CreatedAtUtc,
    IdentityFingerprint? CreatedBy,
    string AdapterId,
    string StatePackageId,
    string? EnvironmentRevisionId);

internal sealed record EnvironmentRevisionFingerprint(
    string Id,
    string WorldId,
    string? ParentRevisionId,
    string CreatedAtUtc,
    IdentityFingerprint? CreatedBy,
    int ManifestSchemaVersion,
    string AdapterId,
    string GameVersion,
    IReadOnlyList<EnvironmentComponentFingerprint> Components,
    IReadOnlyList<KeyValueFingerprint> Configuration);

internal sealed record EnvironmentComponentFingerprint(
    string Kind,
    string Id,
    string? Version,
    string? Source,
    IReadOnlyList<KeyValueFingerprint> Metadata);

internal sealed record IdentityFingerprint(
    string Provider,
    string ExternalId,
    string? DisplayName);

internal sealed record KeyValueFingerprint(string Key, string Value);
