namespace SharedWorlds.Core.Environment;

public sealed record EnvironmentManifest(
    int SchemaVersion,
    string AdapterId,
    string GameVersion,
    IReadOnlyList<EnvironmentComponent> Components,
    IReadOnlyDictionary<string, string> Configuration);

public sealed record EnvironmentComponent(
    string Kind,
    string Id,
    string? Version,
    string? Source,
    IReadOnlyDictionary<string, string>? Metadata = null);
