namespace SharedWorlds.GameAdapters.Palworld.Tests;

[Collection("PalworldOodleEnvironment")]
public sealed class PalworldOodleProductionLookupTests : IDisposable
{
    private const string EnvironmentVariable = "STEWARD_ACCEPTANCE_OODLE_LIB";
    private readonly string? _previousValue = Environment.GetEnvironmentVariable(EnvironmentVariable);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-palworld-oodle-production-{Guid.NewGuid():N}");

    [Fact]
    public void ProductionLookupIgnoresAcceptanceEnvironmentOverride()
    {
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
