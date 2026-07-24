using System.Runtime.InteropServices;

namespace SharedWorlds.GameAdapters.Palworld;

internal interface IPalworldOodleCodec
{
    byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedLength);
}

/// <summary>
/// Loads an Oodle 9 runtime already present on the host for read-only PlM decompression.
/// Steward never redistributes, copies, downloads, or uses Oodle to generate WorldOption bytes.
/// </summary>
internal sealed class PalworldOodleCodec : IPalworldOodleCodec, IDisposable
{
    private const string LibraryFileName = "oo2core_9_win64.dll";
    private const string AcceptanceLibraryEnvironmentVariable = "STEWARD_ACCEPTANCE_OODLE_LIB";
    private const int DecodeThreadPhaseAll = 3;

    private readonly nint _libraryHandle;
    private readonly OodleDecompress _decompress;
    private bool _disposed;

    private PalworldOodleCodec(string libraryPath)
    {
        LibraryPath = Path.GetFullPath(libraryPath);
        _libraryHandle = NativeLibrary.Load(LibraryPath);
        try
        {
            _decompress = LoadExport<OodleDecompress>("OodleLZ_Decompress");
        }
        catch
        {
            NativeLibrary.Free(_libraryHandle);
            throw;
        }
    }

    public string LibraryPath { get; }

    /// <summary>
    /// Acceptance loader. It preserves the explicit operator override used by real-machine probes,
    /// then falls back to the same Palworld-installation-only lookup used by production code.
    /// </summary>
    public static PalworldOodleCodec LoadFromPalworldRoots(params string[] roots)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Palworld WorldOption Oodle path currently supports Windows only.");
        }

        var explicitLibrary = Environment.GetEnvironmentVariable(AcceptanceLibraryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitLibrary))
        {
            if (!Path.IsPathFullyQualified(explicitLibrary))
            {
                throw new FileNotFoundException(
                    $"{AcceptanceLibraryEnvironmentVariable} must be an absolute path.");
            }

            var fullExplicitPath = Path.GetFullPath(explicitLibrary);
            var explicitFile = new FileInfo(fullExplicitPath);
            if (!explicitFile.Exists || (explicitFile.Attributes & FileAttributes.Directory) != 0)
            {
                throw new FileNotFoundException(
                    $"{AcceptanceLibraryEnvironmentVariable} does not reference an existing regular file.",
                    fullExplicitPath);
            }

            return new PalworldOodleCodec(fullExplicitPath);
        }

        return LoadInstalledFromPalworldRoots(roots);
    }

    /// <summary>
    /// Production lookup. Only already-installed files beneath the discovered Palworld client/server
    /// roots are eligible. The acceptance environment-variable override is intentionally ignored.
    /// </summary>
    public static PalworldOodleCodec LoadInstalledFromPalworldRoots(params string[] roots)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The Palworld WorldOption Oodle path currently supports Windows only.");
        }

        ArgumentNullException.ThrowIfNull(roots);
        var candidates = new List<string>();
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            var fullRoot = Path.GetFullPath(root);
            if (!Directory.Exists(fullRoot))
            {
                continue;
            }

            AddCandidate(candidates, Path.Combine(fullRoot, LibraryFileName));
            AddCandidate(candidates, Path.Combine(fullRoot, "Pal", "Binaries", "Win64", LibraryFileName));
            AddCandidate(candidates, Path.Combine(fullRoot, "Engine", "Binaries", "ThirdParty", "Oodle", "Win64", LibraryFileName));

            try
            {
                foreach (var candidate in Directory.EnumerateFiles(
                             fullRoot,
                             LibraryFileName,
                             SearchOption.AllDirectories))
                {
                    AddCandidate(candidates, candidate);
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                DirectoryNotFoundException)
            {
                // A partially inaccessible subtree must not prevent checking other known Palworld roots.
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                return new PalworldOodleCodec(candidate);
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or
                BadImageFormatException or
                EntryPointNotFoundException)
            {
                // Keep looking. We accept only a library exposing the read-only decode entry point.
            }
        }

        throw new FileNotFoundException(
            $"Palworld's current PlM save format requires {LibraryFileName}, but no usable installed copy was found under the discovered Palworld client/server roots. Steward will not search unrelated games, download, copy, or redistribute this runtime.");
    }

    public byte[] Decompress(ReadOnlySpan<byte> compressed, int uncompressedLength)
    {
        ThrowIfDisposed();
        if (compressed.IsEmpty)
        {
            throw new InvalidDataException("Palworld PlM save payload is empty.");
        }

        if (uncompressedLength <= 0)
        {
            throw new InvalidDataException("Palworld PlM save declares an invalid uncompressed length.");
        }

        var source = compressed.ToArray();
        var destination = new byte[uncompressedLength];
        var sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
        var destinationHandle = GCHandle.Alloc(destination, GCHandleType.Pinned);
        try
        {
            var decoded = _decompress(
                sourceHandle.AddrOfPinnedObject(),
                source.Length,
                destinationHandle.AddrOfPinnedObject(),
                destination.Length,
                fuzzSafe: 0,
                checkCrc: 0,
                verbosity: 0,
                decBufBase: nint.Zero,
                decBufSize: 0,
                callback: nint.Zero,
                callbackUserData: nint.Zero,
                decoderMemory: nint.Zero,
                decoderMemorySize: 0,
                threadPhase: DecodeThreadPhaseAll);

            if (decoded != destination.Length)
            {
                throw new InvalidDataException(
                    $"Oodle did not decode the complete Palworld PlM payload ({decoded} of {destination.Length} bytes).");
            }

            return destination;
        }
        finally
        {
            destinationHandle.Free();
            sourceHandle.Free();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        NativeLibrary.Free(_libraryHandle);
        _disposed = true;
    }

    private static void AddCandidate(List<string> candidates, string path)
    {
        if (File.Exists(path))
        {
            candidates.Add(Path.GetFullPath(path));
        }
    }

    private T LoadExport<T>(string name)
        where T : Delegate
    {
        var address = NativeLibrary.GetExport(_libraryHandle, name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint OodleDecompress(
        nint compressedBuffer,
        nint compressedLength,
        nint rawBuffer,
        nint rawLength,
        int fuzzSafe,
        int checkCrc,
        int verbosity,
        nint decBufBase,
        nint decBufSize,
        nint callback,
        nint callbackUserData,
        nint decoderMemory,
        nint decoderMemorySize,
        int threadPhase);
}
