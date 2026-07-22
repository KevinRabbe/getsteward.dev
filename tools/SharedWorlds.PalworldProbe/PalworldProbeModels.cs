sealed record WorldFileState(long Size, DateTime LastWriteTimeUtc);

sealed record WorldObservation(
    IReadOnlyList<string> ChangedFiles,
    DateTimeOffset? FirstChange,
    DateTimeOffset? LastChange,
    DateTimeOffset StabilizedAt,
    bool Stabilized);
