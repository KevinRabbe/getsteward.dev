using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace SharedWorlds.Infrastructure.Remote;

public sealed record VerifiedPackageCacheOptions
{
    public VerifiedPackageCacheOptions(
        long minimumFreeSpaceReserveBytes,
        int copyBufferBytes)
    {
        if (minimumFreeSpaceReserveBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumFreeSpaceReserveBytes));
        }

        if (copyBufferBytes is < 64 * 1024 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(copyBufferBytes));
        }

        MinimumFreeSpaceReserveBytes = minimumFreeSpaceReserveBytes;
        CopyBufferBytes = copyBufferBytes;
    }

    public long MinimumFreeSpaceReserveBytes { get; }
    public int CopyBufferBytes { get; }

    public static VerifiedPackageCacheOptions FirstReleaseDefaults { get; } = new(
        minimumFreeSpaceReserveBytes: 256L * 1024 * 1024,
        copyBufferBytes: 1024 * 1024);
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

/// <summary>
/// Content-addressed immutable package cache used below IWorldStorage.OpenRevisionAsync.
///
/// Presigned URLs and Steward credentials are never persisted. A download is written to a .partial
/// file and may resume using a fresh authorization. The final cache path appears only after exact
/// byte-size and SHA-256 verification.
/// </summary>
public sealed class VerifiedPackageCache
{
    private readonly string _rootDirectory;
    private readonly HttpClient _transferClient;
    private readonly VerifiedPackageCacheOptions _options;
    private readonly ConcurrentDictionary<string, KeyedGate> _gates = new(StringComparer.Ordinal);

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

        using var gate = await AcquireAsync(authorization.ExpectedSha256, cancellationToken);
        var finalPath = GetFinalPath(authorization.ExpectedSha256);
        var partialPath = finalPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        if (File.Exists(finalPath))
        {
            if (await VerifyFileAsync(
                    finalPath,
                    authorization.ExpectedByteSize,
                    authorization.ExpectedSha256,
                    cancellationToken))
            {
                return new VerifiedCachedPackage(
                    finalPath,
                    authorization.ExpectedByteSize,
                    authorization.ExpectedSha256);
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
                        authorization.ExpectedSha256,
                        cancellationToken))
                {
                    PublishVerifiedPartial(partialPath, finalPath);
                    return new VerifiedCachedPackage(
                        finalPath,
                        authorization.ExpectedByteSize,
                        authorization.ExpectedSha256);
                }

                File.Delete(partialPath);
            }
        }

        var existingPartialBytes = File.Exists(partialPath)
            ? new FileInfo(partialPath).Length
            : 0;
        EnsureSufficientFreeSpace(
            finalPath,
            authorization.ExpectedByteSize - existingPartialBytes);

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
                authorization.ExpectedSha256,
                cancellationToken))
        {
            File.Delete(partialPath);
            throw new PackageIntegrityException("Downloaded package SHA-256 did not match the authorized immutable package.");
        }

        PublishVerifiedPartial(partialPath, finalPath);
        return new VerifiedCachedPackage(
            finalPath,
            authorization.ExpectedByteSize,
            authorization.ExpectedSha256);
    }

    public async Task<Stream> OpenVerifiedReadAsync(
        AuthorizedPackageDownload authorization,
        CancellationToken cancellationToken = default)
    {
        var cached = await EnsureAsync(authorization, cancellationToken);
        return new FileStream(
            cached.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: _options.CopyBufferBytes,
            useAsync: true);
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
        await using var destination = new FileStream(
            partialPath,
            mode,
            FileAccess.Write,
            FileShare.None,
            bufferSize: _options.CopyBufferBytes,
            useAsync: true);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await source.CopyToAsync(destination, _options.CopyBufferBytes, cancellationToken);
        await destination.FlushAsync(cancellationToken);
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
            StringComparison.OrdinalIgnoreCase);
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
            // Another process may have published the same content-addressed cache entry. The caller
            // verifies cache entries before use, so retain the existing final and discard our partial.
            File.Delete(partialPath);
        }
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
