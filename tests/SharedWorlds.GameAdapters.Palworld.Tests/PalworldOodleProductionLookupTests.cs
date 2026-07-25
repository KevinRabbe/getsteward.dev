namespace SharedWorlds.GameAdapters.Palworld.Tests;

[Collection("PalworldOodleEnvironment")]
public sealed class PalworldOodleProductionLookupTests : IDisposable
{
    private const string EnvironmentVariable = "STEWARD_ACCEPTANCE_OODLE_LIB";
    private const string LibraryFileName = "oo2core_9_win64.dll";
    private readonly string? _previousValue = Environment.GetEnvironmentVariable(EnvironmentVariable);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-palworld-oodle-production-{Guid.NewGuid():N}");

    [Fact]
    public void ProductionLookupIgnoresAcceptanceEnvironmentOverride()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            $"oo2core_9_win64-{Guid.NewGuid():N}.dll");
        File.WriteAllText(outsideRoot, "not-a-native-library");
        Environment.SetEnvironmentVariable(EnvironmentVariable, outsideRoot);

        try
        {
            var exception = Assert.Throws<FileNotFoundException>(() =>
                PalworldOodleCodec.LoadInstalledFromPalworldRoots(_root));

            Assert.Contains("Palworld client/server roots", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(outsideRoot, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outsideRoot);
        }
    }

    [Fact]
    public void CandidateDiscoveryIncludesRegularInstalledLibrary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var library = Path.Combine(_root, "Pal", "Binaries", "Win64", LibraryFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        File.WriteAllText(library, "not-a-native-library");

        var candidates = PalworldOodleCodec.DiscoverInstalledLibraryCandidates(_root);

        Assert.Contains(Path.GetFullPath(library), candidates, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidateDiscoverySkipsLinkedLibraryFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var outsideLibrary = Path.Combine(
            Path.GetTempPath(),
            $"outside-oodle-{Guid.NewGuid():N}.dll");
        File.WriteAllText(outsideLibrary, "not-a-native-library");
        var linkedLibrary = Path.Combine(_root, LibraryFileName);
        File.CreateSymbolicLink(linkedLibrary, outsideLibrary);
        try
        {
            var candidates = PalworldOodleCodec.DiscoverInstalledLibraryCandidates(_root);

            Assert.DoesNotContain(candidates, candidate =>
                string.Equals(candidate, Path.GetFullPath(linkedLibrary), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(linkedLibrary);
            File.Delete(outsideLibrary);
        }
    }

    [Fact]
    public void CandidateDiscoveryDoesNotTraverseLinkedDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var outsideDirectory = Path.Combine(
            Path.GetTempPath(),
            $"outside-oodle-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);
        var outsideLibrary = Path.Combine(outsideDirectory, LibraryFileName);
        File.WriteAllText(outsideLibrary, "not-a-native-library");
        var linkedDirectory = Path.Combine(_root, "LinkedRuntime");
        Directory.CreateSymbolicLink(linkedDirectory, outsideDirectory);
        try
        {
            var candidates = PalworldOodleCodec.DiscoverInstalledLibraryCandidates(_root);

            Assert.Empty(candidates);
        }
        finally
        {
            Directory.Delete(linkedDirectory);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public void CandidateDiscoverySkipsLinkedRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            $"outside-palworld-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(Path.Combine(outsideRoot, LibraryFileName), "not-a-native-library");
        Directory.CreateSymbolicLink(_root, outsideRoot);
        try
        {
            var candidates = PalworldOodleCodec.DiscoverInstalledLibraryCandidates(_root);

            Assert.Empty(candidates);
        }
        finally
        {
            Directory.Delete(_root);
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void AcceptanceOverrideRejectsLinkedLibraryFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var outsideLibrary = Path.Combine(
            Path.GetTempPath(),
            $"outside-acceptance-oodle-{Guid.NewGuid():N}.dll");
        File.WriteAllText(outsideLibrary, "not-a-native-library");
        var linkedLibrary = Path.Combine(_root, LibraryFileName);
        File.CreateSymbolicLink(linkedLibrary, outsideLibrary);
        Environment.SetEnvironmentVariable(EnvironmentVariable, linkedLibrary);
        try
        {
            var exception = Assert.Throws<FileNotFoundException>(() =>
                PalworldOodleCodec.LoadFromPalworldRoots(_root));

            Assert.Contains("regular non-linked file", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentVariable, null);
            File.Delete(linkedLibrary);
            File.Delete(outsideLibrary);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariable, _previousValue);
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

[CollectionDefinition("PalworldOodleEnvironment", DisableParallelization = true)]
public sealed class PalworldOodleEnvironmentCollection
{
}
