using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace SharedWorlds.Infrastructure.Remote;

public sealed record VerifiedPackageCacheOptions
{
    private const long DefaultMaximumCacheBytes = 40L * 1024 * 1024 * 1024;

    public VerifiedPackageCacheOptions(
        long minimumFreeSpaceReserveBytes,
        int copyBufferBytes,
        long maximumCacheBytes = DefaultMaximumCacheBytes)
    {
        if (minimumFreeSpaceReserveBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumFreeSpaceReserveBytes));
        }

        if (copyBufferBytes is < 64 * 1024 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(copyBufferBytes));
        }

        if (maximumCacheBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCacheBytes));
        }

        MinimumFreeSpaceReserveBytes = minimumFreeSpaceReserveBytes;
        CopyBufferBytes = copyBufferBytes;
        MaximumCacheBytes = maximumCacheBytes;
    }

    public long MinimumFreeSpaceReserveBytes { get; }
    public int CopyBufferBytes { get; }
    public long MaximumCacheBytes { get; }

    public static VerifiedPackageCacheOptions FirstReleaseDefaults { get; } = new(
        minimumFreeSpaceReserveBytes: 256L * 1024 * 1024,
        copyBufferBytes: 1024 * 1024,
        // First release permits up to one 20 GiB State package plus one 20 GiB hosted Environment.
        maximumCacheBytes: DefaultMaximumCacheBytes);
}

public sealed class PackageDownloadAuthorizationExpiredException : IOException
{
    public PackageDownloadAuthorizationExpiredException()
        : base("The package download authorization expired before the transfer could start.")
    {
    }
}

public sealed class PackageIntegrityException : IOException
{
    public PackageIntegrityException(string message)
        : base(message)
    {
    }
}

public sealed class IncompletePackageDownloadException : IOException
{
    public IncompletePackageDownloadException(long expectedByteSize, long actualByteSize)
        : base($"Package download ended before completion. Expected {expectedByteSize} bytes, found {actualByteSize} bytes.")
    {
        ExpectedByteSize = expectedByteSize;
        ActualByteSize = actualByteSize;
    }

    public long ExpectedByteSize { get; }
    public long ActualByteSize { get; }
}

public sealed class InsufficientPackageCacheSpaceException : IOException
{
    public InsufficientPackageCacheSpaceException(long requiredBytes, long availableBytes)
        : base($"Package cache requires {requiredBytes} free bytes, but only {availableBytes} bytes are available.")
    {
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    public long RequiredBytes { get; }
    public long AvailableBytes { get; }
}

public sealed class PackageCacheCapacityException : IOException
{
    public PackageCacheCapacityException(long requiredBytes, long maximumBytes)
        : base($"Package cache requires {requiredBytes} bytes, exceeding its configured maximum of {maximumBytes} bytes.")
    {
        RequiredBytes = requiredBytes;
        MaximumBytes = maximumBytes;
    }

    public long RequiredBytes { get; }
    public long MaximumBytes { get; }
}

/// <summary>
/// Content-addressed immutable package cache used below IWorldStorage.OpenRevisionAsync.
///
/// Presigned URLs and Steward credentials are never persisted. A download is written to a .partial
/// file and may resume using a fresh authorization. The final cache path appears only after exact
/// byte-size and SHA-256 verification. Cache files are disposable/re-downloadable and are bounded
/// independently from durable recovery workspaces, which are never scanned or evicted here.
/// </summary>
public sealed class VerifiedPackageCache
{
    private readonly string _rootDirectory;
    private readonly HttpClient _transferClient;
    private readonly VerifiedPackageCacheOptions _options;
    private readonly ConcurrentDictionary<string, KeyedGate> _gates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cacheMutationGate = new(1, 1);

    public VerifiedPackageCache(
        string rootDirectory,
        HttpClient transferClient,
        VerifiedPackageCacheOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(transferClient);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _transferClient = transferClient;
        _options = options ?? VerifiedPackageCacheOptions.FirstReleaseDefaults;
    }

    public async Task<VerifiedCachedPackage> EnsureAsync(
        AuthorizedPackageDownload authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        using var keyedGate = await AcquireAsync(authorization.ExpectedSha256, cancellationToken);
        using var cacheMutation = await AcquireCacheMutationAsync(cancellationToken);
        return await EnsureLockedAsync(authorization, cancellationToken);
    }

    public async Task<Stream> OpenVerifiedReadAsync(
        AuthorizedPackageDownload authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        using var keyedGate = await AcquireAsync(authorization.ExpectedSha256, cancellationToken);
        using var cacheMutation = await AcquireCacheMutationAsync(cancellationToken);
        var cached = await EnsureLockedAsync(authorization, cancellationToken);
        return new FileStream(
            cached.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: _options.CopyBufferBytes,
            useAsync: true);
    }

    private async Task<VerifiedCachedPackage> EnsureLockedAsync(
        AuthorizedPackageDownload authorization,
        CancellationToken cancellationToken)
    {
        var sha256 = authorization.ExpectedSha256;
        var finalPath = GetFinalPath(sha256);
        var partialPath = finalPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        if (File.Exists(finalPath))
        {
            if (await VerifyFileAsync(
                    finalPath,
                    authorization.ExpectedByteSize,
                    sha256,
                    cancellationToken))
            {
                EnforceCacheCapacity(finalPath, partialPath, additionalBytes: 0);
                TouchCacheEntry(finalPath);
                return new VerifiedCachedPackage(
                    finalPath,
                    authorization.ExpectedByteSize,
                    sha256);
            }

            File.Delete(finalPath);
        }

        if (File.Exists(partialPath))
        {
            var partialLength = new FileInfo(partialPath).Length;
            if (partialLength > authorization.ExpectedByteSize)
            {
                File.Delete(partialPath);
            }
            else if (partialLength == authorization.ExpectedByteSize)
            {
                if (await VerifyFileAsync(
                        partialPath,
                        authorization.ExpectedByteSize,
                        sha256,
                        cancellationToken))
                {
                    EnforceCacheCapacity(finalPath, partialPath, additionalBytes: 0);
                    PublishVerifiedPartial(partialPath, finalPath);
                    await EnsurePublishedFinalIsVerifiedAsync(
                        finalPath,
                        authorization.ExpectedByteSize,
                        sha256,
                        cancellationToken);
                    TouchCacheEntry(finalPath);
                    return new VerifiedCachedPackage(
                        finalPath,
                        authorization.ExpectedByteSize,
                        sha256);
                }

                File.Delete(partialPath);
            }
        }

        var existingPartialBytes = File.Exists(partialPath)
            ? new FileInfo(partialPath).Length
            : 0;
        var remainingDownloadBytes = authorization.ExpectedByteSize - existingPartialBytes;
        EnforceCacheCapacity(finalPath, partialPath, remainingDownloadBytes);
        EnsureSufficientFreeSpace(finalPath, remainingDownloadBytes);

        if (authorization.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new PackageDownloadAuthorizationExpiredException();
        }

        await DownloadAsync(partialPath, authorization, cancellationToken);

        var downloadedBytes = new FileInfo(partialPath).Length;
        if (downloadedBytes < authorization.ExpectedByteSize)
        {
            throw new IncompletePackageDownloadException(
                authorization.ExpectedByteSize,
                downloadedBytes);
        }

        if (downloadedBytes > authorization.ExpectedByteSize)
        {
            File.Delete(partialPath);
            throw new PackageIntegrityException(
                $"Downloaded package exceeded its declared size of {authorization.ExpectedByteSize} bytes.");
        }

        if (!await VerifyFileAsync(
                partialPath,
                authorization.ExpectedByteSize,
                sha256,
                cancellationToken))
        {
            File.Delete(partialPath);
            throw new PackageIntegrityException("Downloaded package SHA-256 did not match the authorized immutable package.");
        }

        PublishVerifiedPartial(partialPath, finalPath);
        await EnsurePublishedFinalIsVerifiedAsync(
            finalPath,
            authorization.ExpectedByteSize,
            sha256,
            cancellationToken);
        TouchCacheEntry(finalPath);
        return new VerifiedCachedPackage(
            finalPath,
            authorization.ExpectedByteSize,
            sha256);
    }

    private async Task DownloadAsync(
        string partialPath,
        AuthorizedPackageDownload authorization,
        CancellationToken cancellationToken)
    {
        var resumeOffset = File.Exists(partialPath)
            ? new FileInfo(partialPath).Length
            : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, authorization.Uri);
        foreach (var header in authorization.RequiredHeaders)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException(
                    $"Required object-storage download header '{header.Key}' cannot be applied to a GET request.");
            }
        }

        if (resumeOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
        }

        using var response = await _transferClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var append = false;
        if (resumeOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent)
        {
            ValidateContentRange(response.Content.Headers.ContentRange, resumeOffset, authorization.ExpectedByteSize);
            append = true;
        }
        else if (resumeOffset > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            // Some compatible endpoints may ignore Range. Restart from zero rather than joining
            // incompatible byte sequences.
            resumeOffset = 0;
        }
        else if (resumeOffset == 0 && response.StatusCode == HttpStatusCode.PartialContent)
        {
            ValidateContentRange(response.Content.Headers.ContentRange, 0, authorization.ExpectedByteSize);
        }
        else
        {
            response.EnsureSuccessStatusCode();
        }

        var mode = append ? FileMode.Append : FileMode.Create;
        var remainingAuthorizedBytes = authorization.ExpectedByteSize - resumeOffset;
        bool exceededAuthorizedSize;
        await using (var destination = new FileStream(
                         partialPath,
                         mode,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: _options.CopyBufferBytes,
                         useAsync: true))
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            exceededAuthorizedSize = await CopyWithByteCeilingAsync(
                source,
                destination,
                remainingAuthorizedBytes,
                _options.CopyBufferBytes,
                cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }

        if (exceededAuthorizedSize)
        {
            File.Delete(partialPath);
            throw new PackageIntegrityException(
                $"Downloaded package exceeded its declared size of {authorization.ExpectedByteSize} bytes.");
        }
    }

    private static async Task<bool> CopyWithByteCeilingAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        int bufferBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes < 0)
        {
            throw new PackageIntegrityException("Package resume offset exceeded the authorized immutable package size.");
        }

        var buffer = new byte[bufferBytes];
        var remaining = maximumBytes;
        while (true)
        {
            var requested = remaining < buffer.Length
                ? checked((int)remaining + 1)
                : buffer.Length;
            var read = await source.ReadAsync(
                buffer.AsMemory(0, requested),
                cancellationToken);
            if (read == 0)
            {
                return false;
            }

            var writable = (int)Math.Min(read, remaining);
            if (writable > 0)
            {
                await destination.WriteAsync(
                    buffer.AsMemory(0, writable),
                    cancellationToken);
                remaining -= writable;
            }

            if (read > writable)
            {
                return true;
            }
        }
    }

    private async Task<bool> VerifyFileAsync(
        string path,
        long expectedByteSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length != expectedByteSize)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: _options.CopyBufferBytes,
            useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[_options.CopyBufferBytes];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return string.Equals(
            Convert.ToHexString(hash.GetHashAndReset()),
            expectedSha256,
            StringComparison.Ordinal);
    }

    private async Task EnsurePublishedFinalIsVerifiedAsync(
        string finalPath,
        long expectedByteSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (await VerifyFileAsync(
                finalPath,
                expectedByteSize,
                expectedSha256,
                cancellationToken))
        {
            return;
        }

        throw new PackageIntegrityException(
            "Published package cache entry did not match the authorized immutable package.");
    }

    private static void ValidateContentRange(
        ContentRangeHeaderValue? range,
        long expectedStart,
        long expectedTotalBytes)
    {
        if (range?.From != expectedStart ||
            range.To is null ||
            range.To < expectedStart ||
            range.To >= expectedTotalBytes ||
            range.Length is { } totalLength && totalLength != expectedTotalBytes)
        {
            throw new PackageIntegrityException("Object-storage server returned an invalid resume Content-Range.");
        }
    }

    private void EnforceCacheCapacity(
        string protectedFinalPath,
        string protectedPartialPath,
        long additionalBytes)
    {
        if (additionalBytes < 0)
        {
            throw new PackageCacheCapacityException(additionalBytes, _options.MaximumCacheBytes);
        }

        var cacheRoot = Path.Combine(_rootDirectory, "packages", "sha256");
        if (!Directory.Exists(cacheRoot))
        {
            if (additionalBytes > _options.MaximumCacheBytes)
            {
                throw new PackageCacheCapacityException(additionalBytes, _options.MaximumCacheBytes);
            }

            return;
        }

        var protectedFinal = Path.GetFullPath(protectedFinalPath);
        var protectedPartial = Path.GetFullPath(protectedPartialPath);
        var entries = Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".package", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .ToArray();

        long currentBytes = 0;
        foreach (var entry in entries)
        {
            currentBytes = checked(currentBytes + entry.Length);
        }

        var requiredBytes = checked(currentBytes + additionalBytes);
        if (requiredBytes <= _options.MaximumCacheBytes)
        {
            return;
        }

        var candidates = entries
            .Where(entry =>
            {
                var fullPath = Path.GetFullPath(entry.FullName);
                return !string.Equals(fullPath, protectedFinal, StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(fullPath, protectedPartial, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(entry => entry.LastWriteTimeUtc)
            .ThenBy(entry => entry.FullName, StringComparer.Ordinal)
            .ToArray();

        foreach (var candidate in candidates)
        {
            var candidateBytes = candidate.Length;
            if (!TryDeleteCacheEntry(candidate.FullName))
            {
                continue;
            }

            currentBytes = Math.Max(0, currentBytes - candidateBytes);
            requiredBytes = checked(currentBytes + additionalBytes);
            if (requiredBytes <= _options.MaximumCacheBytes)
            {
                return;
            }
        }

        throw new PackageCacheCapacityException(requiredBytes, _options.MaximumCacheBytes);
    }

    private static bool TryDeleteCacheEntry(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            File.Delete(path);
            return !File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TouchCacheEntry(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void EnsureSufficientFreeSpace(string destinationPath, long remainingDownloadBytes)
    {
        if (remainingDownloadBytes <= 0)
        {
            return;
        }

        var required = checked(remainingDownloadBytes + _options.MinimumFreeSpaceReserveBytes);
        var root = Path.GetPathRoot(Path.GetFullPath(destinationPath));
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        try
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < required)
            {
                throw new InsufficientPackageCacheSpaceException(required, drive.AvailableFreeSpace);
            }
        }
        catch (DriveNotFoundException)
        {
            // Some network/virtual filesystems do not expose DriveInfo. Integrity verification still
            // protects the cache; free-space preflight is best-effort on those filesystems.
        }
    }

    private string GetFinalPath(string sha256)
    {
        var normalized = sha256.ToLowerInvariant();
        return Path.Combine(
            _rootDirectory,
            "packages",
            "sha256",
            normalized[..2],
            normalized + ".package");
    }

    private static void PublishVerifiedPartial(string partialPath, string finalPath)
    {
        try
        {
            File.Move(partialPath, finalPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            // Another process may have published the same content-addressed entry. Discard our
            // duplicate partial, then the caller re-verifies the existing final before returning it.
            File.Delete(partialPath);
        }
    }

    private async ValueTask<CacheMutationLease> AcquireCacheMutationAsync(
        CancellationToken cancellationToken)
    {
        await _cacheMutationGate.WaitAsync(cancellationToken);
        return new CacheMutationLease(_cacheMutationGate);
    }

    private async ValueTask<KeyedGateLease> AcquireAsync(
        string key,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var gate = _gates.GetOrAdd(key, static _ => new KeyedGate());
            Interlocked.Increment(ref gate.ReferenceCount);
            if (_gates.TryGetValue(key, out var current) && ReferenceEquals(gate, current))
            {
                try
                {
                    await gate.Semaphore.WaitAsync(cancellationToken);
                    return new KeyedGateLease(this, key, gate);
                }
                catch
                {
                    ReleaseReference(key, gate, releaseSemaphore: false);
                    throw;
                }
            }

            ReleaseReference(key, gate, releaseSemaphore: false);
        }
    }

    private void ReleaseReference(string key, KeyedGate gate, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            gate.Semaphore.Release();
        }

        if (Interlocked.Decrement(ref gate.ReferenceCount) == 0)
        {
            _gates.TryRemove(new KeyValuePair<string, KeyedGate>(key, gate));
        }
    }

    private sealed class CacheMutationLease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public CacheMutationLease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }

    private sealed class KeyedGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount;
    }

    private sealed class KeyedGateLease : IDisposable
    {
        private VerifiedPackageCache? _owner;
        private readonly string _key;
        private readonly KeyedGate _gate;

        public KeyedGateLease(VerifiedPackageCache owner, string key, KeyedGate gate)
        {
            _owner = owner;
            _key = key;
            _gate = gate;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReleaseReference(_key, _gate, releaseSemaphore: true);
        }
    }
}
