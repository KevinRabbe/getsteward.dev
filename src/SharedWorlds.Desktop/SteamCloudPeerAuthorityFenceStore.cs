using System.Globalization;
using System.Text.Json;
using System.Windows.Threading;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;
using Steamworks;

namespace SharedWorlds.Desktop;

/// <summary>
/// Stores only Steward's tiny per-account peer-authority restart fence in Steam Cloud. Live writer
/// authority remains fenced by the active Steam lobby owner/generation. World payloads, environments,
/// history, and saves never pass through this store.
/// </summary>
internal sealed class SteamCloudPeerAuthorityFenceStore : IPeerAuthorityActiveRevisionFenceStore
{
    private const int SchemaVersion = 2;
    private const int MaximumFenceBytes = 16 * 1024;
    private const int MaximumInstallationIdLength = 256;
    private const string FilePrefix = "steward-authority-";
    private const string FileSuffix = ".json";

    private readonly SteamPlatformRuntime _platform;
    private readonly string _installationId;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public SteamCloudPeerAuthorityFenceStore(
        SteamPlatformRuntime platform,
        string installationId)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        installationId = installationId.Trim();
        if (installationId.Length > MaximumInstallationIdLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(installationId),
                $"Steward installation identity cannot exceed {MaximumInstallationIdLength} characters.");
        }

        _platform = platform;
        _installationId = installationId;
    }

    public Task<PeerAuthorityFence?> LoadAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
        => InvokeSteamAsync(
            () => LoadCore(worldId),
            cancellationToken);

    public async Task SaveAsync(
        PeerAuthorityFence fence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fence);
        ValidateFenceSemantics(fence);

        await InvokeSteamAsync(
            () =>
            {
                EnsureCloudAvailable();
                var current = LoadCore(fence.WorldId);
                ValidateMonotonicTransition(current, fence);
                WriteAndVerifyCore(fence);
            },
            cancellationToken);
    }

    public async Task<PeerAuthorityFence> AdvanceActiveRevisionAsync(
        WorldId worldId,
        UserIdentity holder,
        ulong generation,
        RevisionId expectedStateRevisionId,
        RevisionId nextStateRevisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(holder);
        if (generation == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "Active peer authority generation must be nonzero.");
        }

        if (expectedStateRevisionId == nextStateRevisionId)
        {
            throw new ArgumentException(
                "Active peer authority revision advancement requires a different next revision.",
                nameof(nextStateRevisionId));
        }

        var next = new PeerAuthorityFence(
            worldId,
            holder,
            generation,
            nextStateRevisionId,
            PeerAuthorityFenceState.Active,
            DateTimeOffset.UtcNow);
        ValidateFenceSemantics(next);

        return await InvokeSteamAsync(
            () =>
            {
                EnsureCloudAvailable();
                var current = LoadCore(worldId)
                    ?? throw new InvalidDataException(
                        $"Steam Cloud has no Active authority fence to advance for World '{worldId}'.");

                if (EquivalentFence(current, next))
                {
                    return current;
                }

                if (current.State != PeerAuthorityFenceState.Active ||
                    current.Generation != generation ||
                    current.StateRevisionId != expectedStateRevisionId ||
                    !SameUser(current.Holder, holder))
                {
                    throw new InvalidDataException(
                        $"Steam Cloud authority fence for World '{worldId}' no longer matches the exact Active revision Steward expected to advance.");
                }

                WriteAndVerifyCore(next);
                return LoadCore(worldId)
                    ?? throw new IOException(
                        $"Steam Cloud lost Steward's authority fence after advancing World '{worldId}'.");
            },
            cancellationToken);
    }

    private void WriteAndVerifyCore(PeerAuthorityFence fence)
    {
        var document = new FenceDocument(
            SchemaVersion,
            _platform.LocalSteamId.m_SteamID.ToString(CultureInfo.InvariantCulture),
            _installationId,
            fence.WorldId.ToString(),
            fence.Holder.ExternalId,
            fence.Generation,
            fence.StateRevisionId.ToString(),
            fence.State,
            fence.UpdatedAt.ToUnixTimeMilliseconds());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, _jsonOptions);
        if (bytes.Length <= 0 || bytes.Length > MaximumFenceBytes)
        {
            throw new InvalidDataException(
                $"Steam Cloud authority fence is {bytes.Length} bytes and exceeds Steward's {MaximumFenceBytes}-byte bound.");
        }

        var fileName = GetFileName(fence.WorldId);
        if (!SteamRemoteStorage.FileWrite(fileName, bytes, bytes.Length))
        {
            throw new IOException(
                $"Steam Cloud rejected Steward's authority fence write for World '{fence.WorldId}'.");
        }

        var verified = LoadCore(fence.WorldId)
            ?? throw new IOException(
                $"Steam Cloud did not return Steward's authority fence after writing World '{fence.WorldId}'.");
        if (!EquivalentFence(verified, fence))
        {
            throw new IOException(
                $"Steam Cloud authority fence verification failed after writing World '{fence.WorldId}'.");
        }
    }

    private PeerAuthorityFence? LoadCore(WorldId worldId)
    {
        EnsureCloudAvailable();
        var fileName = GetFileName(worldId);
        if (!SteamRemoteStorage.FileExists(fileName))
        {
            return null;
        }

        var length = SteamRemoteStorage.GetFileSize(fileName);
        if (length <= 0 || length > MaximumFenceBytes)
        {
            throw new InvalidDataException(
                $"Steam Cloud authority fence for World '{worldId}' has invalid length '{length}'.");
        }

        var bytes = new byte[length];
        var read = SteamRemoteStorage.FileRead(fileName, bytes, bytes.Length);
        if (read != bytes.Length)
        {
            throw new InvalidDataException(
                $"Steam Cloud authority fence for World '{worldId}' returned {read} bytes, expected {bytes.Length}.");
        }

        FenceDocument document;
        try
        {
            document = JsonSerializer.Deserialize<FenceDocument>(bytes, _jsonOptions)
                ?? throw new InvalidDataException(
                    $"Steam Cloud authority fence for World '{worldId}' was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Steam Cloud authority fence for World '{worldId}' is malformed.",
                exception);
        }

        return ParseDocument(worldId, document);
    }

    private PeerAuthorityFence ParseDocument(
        WorldId expectedWorldId,
        FenceDocument document)
    {
        if (document.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Steam Cloud authority fence for World '{expectedWorldId}' uses unsupported schema '{document.SchemaVersion}'.");
        }

        var localSteamId = _platform.LocalSteamId.m_SteamID.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(document.AccountSteamId, localSteamId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Steam Cloud authority fence for World '{expectedWorldId}' belongs to a different Steam account.");
        }

        if (string.IsNullOrWhiteSpace(document.WriterInstallationId) ||
            document.WriterInstallationId.Length > MaximumInstallationIdLength)
        {
            throw new InvalidDataException(
                $"Steam Cloud authority fence for World '{expectedWorldId}' has invalid Steward installation identity.");
        }

        if (!Guid.TryParseExact(document.WorldId, "N", out var worldGuid) ||
            new WorldId(worldGuid) != expectedWorldId)
        {
            throw new InvalidDataException(
                $"Steam Cloud authority fence is stored under the wrong World identity '{document.WorldId}'.");
        }

        if (!ulong.TryParse(
                document.HolderSteamId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var holderSteamId) ||
            holderSteamId == 0)
        {
            throw new InvalidDataException(
                $"Steam Cloud authority fence for World '{expectedWorldId}' has invalid holder SteamID64.");
        }

        if (document.Generation == 0)
        {
            throw new InvalidDataException(
                $"Steam Cloud authority fence for World '{expectedWorldId}' has zero generation.");
        }

        if (!Guid.TryParseExact(document.StateRevisionId, "N", out var revisionGuid))
        {
            throw new InvalidDataException(
                $"Steam Cloud authority fence for World '{expectedWorldId}' has invalid state revision identity.");
        }

        if (!Enum.IsDefined(document.State))
        {
            throw new InvalidDataException(
                $"Steam Cloud authority fence for World '{expectedWorldId}' has unsupported state '{document.State}'.");
        }

        if (document.State == PeerAuthorityFenceState.Active &&
            !string.Equals(
                document.WriterInstallationId,
                _installationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Steam Cloud Active authority for World '{expectedWorldId}' belongs to a different Steward installation.");
        }

        DateTimeOffset updatedAt;
        try
        {
            updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(document.UpdatedAtUnixMilliseconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                $"Steam Cloud authority fence for World '{expectedWorldId}' has invalid timestamp.",
                exception);
        }

        var fence = new PeerAuthorityFence(
            expectedWorldId,
            new UserIdentity("steam", document.HolderSteamId, document.HolderSteamId),
            document.Generation,
            new RevisionId(revisionGuid),
            document.State,
            updatedAt);
        ValidateFenceSemantics(fence);
        return fence;
    }

    private void EnsureCloudAvailable()
    {
        if (!SteamRemoteStorage.IsCloudEnabledForAccount())
        {
            throw new InvalidOperationException(
                "Steam Cloud is disabled for this Steam account. Steward peer restart fencing is unavailable because stale-device state cannot be synchronized safely between sessions.");
        }

        if (!SteamRemoteStorage.IsCloudEnabledForApp())
        {
            throw new InvalidOperationException(
                "Steam Cloud is disabled for Steward. Peer restart authority is unavailable until Cloud storage is enabled for the app.");
        }
    }

    private void ValidateFenceSemantics(PeerAuthorityFence fence)
    {
        if (!string.Equals(fence.Holder.Provider, "steam", StringComparison.OrdinalIgnoreCase) ||
            !ulong.TryParse(
                fence.Holder.ExternalId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var holderSteamId) ||
            holderSteamId == 0)
        {
            throw new InvalidDataException(
                $"Authority fence for World '{fence.WorldId}' requires a valid Steam holder identity.");
        }

        if (fence.Generation == 0)
        {
            throw new InvalidDataException(
                $"Authority fence for World '{fence.WorldId}' cannot use generation zero.");
        }

        if (!Enum.IsDefined(fence.State))
        {
            throw new InvalidDataException(
                $"Authority fence for World '{fence.WorldId}' has unsupported state '{fence.State}'.");
        }

        if (fence.State == PeerAuthorityFenceState.Active &&
            holderSteamId != _platform.LocalSteamId.m_SteamID)
        {
            throw new InvalidDataException(
                $"Authority fence for World '{fence.WorldId}' cannot mark another Steam account as Active locally.");
        }

        if (fence.State == PeerAuthorityFenceState.Relinquishing &&
            holderSteamId == _platform.LocalSteamId.m_SteamID)
        {
            throw new InvalidDataException(
                $"Authority fence for World '{fence.WorldId}' cannot relinquish authority to the same Steam account.");
        }
    }

    private static void ValidateMonotonicTransition(
        PeerAuthorityFence? current,
        PeerAuthorityFence next)
    {
        if (current is null)
        {
            return;
        }

        if (next.Generation < current.Generation)
        {
            throw new InvalidDataException(
                $"Authority fence for World '{next.WorldId}' cannot move backward from generation {current.Generation} to {next.Generation}.");
        }

        if (next.Generation > current.Generation)
        {
            return;
        }

        if (!SameUser(current.Holder, next.Holder) ||
            current.StateRevisionId != next.StateRevisionId)
        {
            throw new InvalidDataException(
                $"Authority fence for World '{next.WorldId}' conflicts with an existing record at generation {next.Generation}.");
        }

        var allowed = current.State == next.State ||
                      current.State == PeerAuthorityFenceState.Relinquishing &&
                      next.State == PeerAuthorityFenceState.Observed;
        if (!allowed)
        {
            throw new InvalidDataException(
                $"Authority fence for World '{next.WorldId}' cannot transition from '{current.State}' to '{next.State}' at the same generation.");
        }
    }

    private static bool EquivalentFence(
        PeerAuthorityFence left,
        PeerAuthorityFence right)
        => left.WorldId == right.WorldId &&
           SameUser(left.Holder, right.Holder) &&
           left.Generation == right.Generation &&
           left.StateRevisionId == right.StateRevisionId &&
           left.State == right.State;

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);

    private static string GetFileName(WorldId worldId)
        => $"{FilePrefix}{worldId}{FileSuffix}";

    private async Task<T> InvokeSteamAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (_platform.Dispatcher.CheckAccess())
        {
            return action();
        }

        return await _platform.Dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken).Task;
    }

    private async Task InvokeSteamAsync(
        Action action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (_platform.Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await _platform.Dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken).Task;
    }

    private sealed record FenceDocument(
        int SchemaVersion,
        string AccountSteamId,
        string WriterInstallationId,
        string WorldId,
        string HolderSteamId,
        ulong Generation,
        string StateRevisionId,
        PeerAuthorityFenceState State,
        long UpdatedAtUnixMilliseconds);
}
