using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Infrastructure.Remote;

public sealed class StewardInitialWorldPublicationException : InvalidOperationException
{
    public StewardInitialWorldPublicationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Publishes the immutable canonical snapshot of an existing local World into Steward. The operation
/// is deliberately retryable across crashes: backend World identity, environment metadata, and state
/// package publication are all immutable/idempotent under the same IDs. This service never changes the
/// local World's SharingMode; Desktop journals remote-authority intent locally before calling it.
/// </summary>
public sealed class StewardInitialWorldPublisher
{
    private readonly StewardWorldCreationClient _creation;
    private readonly StewardWorldMetadataClient _metadata;
    private readonly StewardPackageUploadClient _uploads;
    private readonly IStewardAccessTokenProvider _accessTokens;

    public StewardInitialWorldPublisher(
        StewardWorldCreationClient creation,
        StewardWorldMetadataClient metadata,
        StewardPackageUploadClient uploads,
        IStewardAccessTokenProvider accessTokens)
    {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(accessTokens);
        _creation = creation;
        _metadata = metadata;
        _uploads = uploads;
        _accessTokens = accessTokens;
    }

    public async Task<StewardRemoteWorldMetadata> PublishAsync(
        World sourceWorld,
        EnvironmentRevision environment,
        StateRevision state,
        Stream statePackage,
        UserIdentity authenticatedUser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceWorld);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(statePackage);
        ArgumentNullException.ThrowIfNull(authenticatedUser);

        var stateId = sourceWorld.CurrentStateRevisionId
            ?? throw Invalid("StateHeadMissing", "A World without a current state revision cannot be shared.");
        var environmentId = sourceWorld.CurrentEnvironmentRevisionId
            ?? throw Invalid("EnvironmentHeadMissing", "A World without a current environment revision cannot be shared.");
        if (state.Id != stateId || state.WorldId != sourceWorld.Id)
        {
            throw Invalid("StateRevisionMismatch", "The local state revision does not match the World head being shared.");
        }

        if (environment.Id != environmentId || environment.WorldId != sourceWorld.Id)
        {
            throw Invalid("EnvironmentRevisionMismatch", "The local environment revision does not match the World head being shared.");
        }

        if (!string.Equals(state.AdapterId, sourceWorld.GameAdapterId, StringComparison.Ordinal) ||
            !string.Equals(environment.Manifest.AdapterId, sourceWorld.GameAdapterId, StringComparison.Ordinal))
        {
            throw Invalid("AdapterMismatch", "The local World, state revision, and environment revision must belong to the same game adapter.");
        }

        if (!statePackage.CanRead || !statePackage.CanSeek)
        {
            throw new ArgumentException("Initial World publication requires a readable seekable state package stream.", nameof(statePackage));
        }

        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        var remote = await _metadata.GetWorldAsync(sourceWorld.Id, accessToken, cancellationToken);
        if (remote is null)
        {
            await _creation.CreateAsync(sourceWorld, accessToken, cancellationToken);
            remote = await _metadata.GetWorldAsync(sourceWorld.Id, accessToken, cancellationToken)
                ?? throw Invalid(
                    "WorldCreationNotVisible",
                    "Steward accepted World creation but the created World could not be read back.");
        }

        ValidateRemoteIdentity(remote, sourceWorld, authenticatedUser);

        var environmentStatus = await _metadata.PublishEnvironmentRevisionAsync(
            sourceWorld.Id,
            environmentId,
            environment.Manifest,
            accessToken,
            cancellationToken);
        if (environmentStatus is not (RemoteEnvironmentPublishStatus.Published or
            RemoteEnvironmentPublishStatus.AlreadyPublished))
        {
            throw Invalid(
                "EnvironmentPublicationFailed",
                $"Steward rejected the immutable environment revision with status '{environmentStatus}'.");
        }

        statePackage.Position = 0;
        var upload = await _uploads.UploadAsync(
            sourceWorld.Id,
            stateId,
            RemotePackageKind.State,
            statePackage,
            environmentId,
            accessToken,
            cancellationToken);
        if (upload.Status is not (RemotePackageUploadStatus.Published or
            RemotePackageUploadStatus.AlreadyPublished))
        {
            throw Invalid(
                "StatePublicationFailed",
                $"Steward rejected the immutable state package with status '{upload.Status}'.");
        }

        var current = await _metadata.GetCurrentRevisionAsync(
            sourceWorld.Id,
            accessToken,
            cancellationToken)
            ?? throw Invalid(
                "PublishedWorldMissing",
                "Steward could not read back the World after initial publication completed.");
        ValidateRemoteIdentity(current.World, sourceWorld, authenticatedUser);
        if (current.State is null || current.State.RevisionId != stateId)
        {
            throw Invalid(
                "StatePublicationUnverified",
                "Steward did not expose the expected immutable state revision after upload.");
        }

        if (current.State.RequiredEnvironmentRevisionId != environmentId)
        {
            throw Invalid(
                "StateEnvironmentMismatch",
                "The published state revision does not require the exact environment that was shared with it.");
        }

        var publishedEnvironment = await _metadata.GetEnvironmentRevisionAsync(
            sourceWorld.Id,
            environmentId,
            accessToken,
            cancellationToken);
        if (publishedEnvironment?.Manifest is null ||
            !EnvironmentManifestsEqual(environment.Manifest, publishedEnvironment.Manifest))
        {
            throw Invalid(
                "EnvironmentPublicationUnverified",
                "Steward did not read back the same structured environment manifest after publication.");
        }

        return current.World;
    }

    private static void ValidateRemoteIdentity(
        StewardRemoteWorldMetadata remote,
        World source,
        UserIdentity authenticatedUser)
    {
        if (remote.WorldId != source.Id ||
            !string.Equals(remote.AdapterId, source.GameAdapterId, StringComparison.Ordinal) ||
            !string.Equals(remote.DisplayName, source.Name, StringComparison.Ordinal) ||
            remote.CurrentStateRevisionId != source.CurrentStateRevisionId ||
            remote.CurrentEnvironmentRevisionId != source.CurrentEnvironmentRevisionId)
        {
            throw Invalid(
                "ExistingWorldConflict",
                "A backend World with this ID already exists but does not match the local World's immutable sharing snapshot.");
        }

        if (!string.Equals(remote.AccessManager.Provider, authenticatedUser.Provider, StringComparison.Ordinal) ||
            !string.Equals(remote.AccessManager.ExternalId, authenticatedUser.ExternalId, StringComparison.Ordinal))
        {
            throw Invalid(
                "AccessManagerMismatch",
                "The existing backend World with this ID is managed by a different authenticated identity.");
        }
    }

    private static bool EnvironmentManifestsEqual(EnvironmentManifest left, EnvironmentManifest right)
    {
        if (left.SchemaVersion != right.SchemaVersion ||
            !string.Equals(left.AdapterId, right.AdapterId, StringComparison.Ordinal) ||
            !string.Equals(left.GameVersion, right.GameVersion, StringComparison.Ordinal) ||
            !DictionaryEqual(left.Configuration, right.Configuration) ||
            left.Components.Count != right.Components.Count)
        {
            return false;
        }

        var leftComponents = left.Components.OrderBy(ComponentKey, StringComparer.Ordinal).ToArray();
        var rightComponents = right.Components.OrderBy(ComponentKey, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < leftComponents.Length; index++)
        {
            var a = leftComponents[index];
            var b = rightComponents[index];
            if (!string.Equals(a.Kind, b.Kind, StringComparison.Ordinal) ||
                !string.Equals(a.Id, b.Id, StringComparison.Ordinal) ||
                !string.Equals(a.Version, b.Version, StringComparison.Ordinal) ||
                !string.Equals(a.Source, b.Source, StringComparison.Ordinal) ||
                !DictionaryEqual(a.Metadata, b.Metadata))
            {
                return false;
            }
        }

        return true;
    }

    private static string ComponentKey(EnvironmentComponent component)
        => string.Join(
            "\u001f",
            component.Kind,
            component.Id,
            component.Version ?? string.Empty,
            component.Source ?? string.Empty,
            component.Metadata is null
                ? string.Empty
                : string.Join(
                    "\u001e",
                    component.Metadata
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => $"{pair.Key}\u001d{pair.Value}")));

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value) ||
                !string.Equals(pair.Value, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static StewardInitialWorldPublicationException Invalid(string code, string message)
        => new(code, message);
}
