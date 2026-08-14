namespace SharedWorlds.Core.Domain;

/// <summary>
/// Durable identity for locating a prepared World after process restart. This is deliberately not
/// an absolute runtime path. SafeWorld-managed workspaces resolve from WorkspaceId and the current
/// configured storage layout; native locations carry only adapter-owned stable identity metadata.
/// </summary>
public sealed record PreparedWorldRecoveryLocation
{
    private const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }
    public required PreparedWorldRecoveryLocationKind Kind { get; init; }
    public IReadOnlyDictionary<string, string>? NativeIdentity { get; init; }

    public static PreparedWorldRecoveryLocation Managed()
        => new()
        {
            SchemaVersion = CurrentSchemaVersion,
            Kind = PreparedWorldRecoveryLocationKind.SafeWorldManaged
        };

    public static PreparedWorldRecoveryLocation Native(
        IReadOnlyDictionary<string, string> identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Count == 0)
        {
            throw new ArgumentException(
                "Native prepared-World recovery identity must not be empty.",
                nameof(identity));
        }

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in identity)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                throw new ArgumentException(
                    "Native prepared-World recovery identity keys and values must be non-empty.",
                    nameof(identity));
            }

            if (!copy.TryAdd(pair.Key, pair.Value))
            {
                throw new ArgumentException(
                    $"Native prepared-World recovery identity contains duplicate key '{pair.Key}'.",
                    nameof(identity));
            }
        }

        return new PreparedWorldRecoveryLocation
        {
            SchemaVersion = CurrentSchemaVersion,
            Kind = PreparedWorldRecoveryLocationKind.NativeGame,
            NativeIdentity = copy
        };
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported prepared-World recovery location schema version '{SchemaVersion}'.");
        }

        switch (Kind)
        {
            case PreparedWorldRecoveryLocationKind.SafeWorldManaged:
                if (NativeIdentity is { Count: > 0 })
                {
                    throw new InvalidDataException(
                        "A SafeWorld-managed recovery location must not carry native identity metadata.");
                }

                break;

            case PreparedWorldRecoveryLocationKind.NativeGame:
                if (NativeIdentity is not { Count: > 0 })
                {
                    throw new InvalidDataException(
                        "A native-game recovery location must carry stable adapter identity metadata.");
                }

                foreach (var pair in NativeIdentity)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                    {
                        throw new InvalidDataException(
                            "Native prepared-World recovery identity keys and values must be non-empty.");
                    }
                }

                break;

            default:
                throw new InvalidDataException(
                    $"Unknown prepared-World recovery location kind '{Kind}'.");
        }
    }
}

public enum PreparedWorldRecoveryLocationKind
{
    SafeWorldManaged = 1,
    NativeGame = 2
}
