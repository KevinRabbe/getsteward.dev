namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidManagedLauncherTests : IDisposable
{
    private const string SourceBatch =
        "@setlocal enableextensions\r\n" +
        "@cd /d \"%~dp0\"\r\n" +
        "SET PZ_CLASSPATH=java/example.jar;java/\r\n" +
        "\".\\jre64\\bin\\java.exe\" -Djava.awt.headless=true -cp %PZ_CLASSPATH% zombie.network.GameServer -statistic 0 %1 %2\r\n" +
        "PAUSE\r\n";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-pz-managed-launcher-{Guid.NewGuid():N}");
    private readonly List<string> _ownedOperationRoots = [];

    [Fact]
    public void TransformBindsServerRootCacheAndServerNameWithoutChangingOtherLines()
    {
        var inputs = Inputs(
            @"C:\Steam Library\Project Zomboid Dedicated Server\StartServer64.bat",
            @"C:\Steam Library\Project Zomboid Dedicated Server",
            @"C:\Users\Test User\AppData\Local\Steward\workspaces\project-zomboid\abc\Zomboid",
            "steward test");

        var result = ProjectZomboidManagedLauncherWriter.Transform(SourceBatch, inputs);

        Assert.Contains(
            "@cd /d \"C:\\Steam Library\\Project Zomboid Dedicated Server\"\r\n",
            result,
            StringComparison.Ordinal);
        Assert.Contains(
            "zombie.network.GameServer -statistic 0 \"-cachedir=C:\\Users\\Test User\\AppData\\Local\\Steward\\workspaces\\project-zomboid\\abc\\Zomboid\" -servername \"steward test\"",
            result,
            StringComparison.Ordinal);
        Assert.False(result.Contains("%~dp0", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.Contains("%1 %2", StringComparison.Ordinal));
        Assert.Contains("SET PZ_CLASSPATH=java/example.jar;java/\r\n", result, StringComparison.Ordinal);
        Assert.EndsWith("PAUSE\r\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformRejectsUnknownWorkingDirectoryShape()
    {
        var source = SourceBatch.Replace("@cd /d \"%~dp0\"", "@cd /d %~dp0", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidManagedLauncherWriter.Transform(source, Inputs()));

        Assert.Contains("quoted '%~dp0'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformRejectsMultipleGameServerInvocations()
    {
        var source = SourceBatch +
                     "\".\\jre64\\bin\\java.exe\" -cp %PZ_CLASSPATH% zombie.network.GameServer %1 %2\r\n";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidManagedLauncherWriter.Transform(source, Inputs()));

        Assert.Contains("exactly one zombie.network.GameServer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformRejectsSourceThatAlreadyOwnsServerArguments()
    {
        var source = SourceBatch.Replace(
            "%1 %2",
            "%1 %2 -servername existing",
            StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidManagedLauncherWriter.Transform(source, Inputs()));

        Assert.Contains("already contains Steward-owned game arguments", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unsafe%name")]
    [InlineData("unsafe!name")]
    [InlineData("unsafe\"name")]
    [InlineData("unsafe\r\nname")]
    public void TransformRejectsUnsafeBatchServerName(string serverName)
    {
        var inputs = Inputs(serverName: serverName);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidManagedLauncherWriter.Transform(SourceBatch, inputs));

        Assert.Contains("characters Steward will not embed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaterializeWritesManagedCopyBesideOwnedCacheWithoutChangingSteamLauncher()
    {
        Directory.CreateDirectory(_root);
        var serverRoot = Path.Combine(_root, "Project Zomboid Dedicated Server");
        Directory.CreateDirectory(serverRoot);
        var sourcePath = Path.Combine(serverRoot, "StartServer64.bat");
        File.WriteAllText(sourcePath, SourceBatch);
        var cacheDirectory = CreateOwnedCacheDirectory();
        var inputs = Inputs(sourcePath, serverRoot, cacheDirectory, "steward-test");

        var launcher = ProjectZomboidManagedLauncherWriter.Materialize(inputs);

        Assert.Equal(serverRoot, launcher.WorkingDirectory);
        Assert.Equal(
            Path.Combine(Directory.GetParent(cacheDirectory)!.FullName, "StartServer64.sharedworlds.bat"),
            launcher.Path);
        Assert.True(File.Exists(launcher.Path));
        Assert.Equal(SourceBatch, File.ReadAllText(sourcePath));
        var managedText = File.ReadAllText(launcher.Path);
        Assert.Contains("-cachedir=", managedText, StringComparison.Ordinal);
        Assert.Contains("-servername \"steward-test\"", managedText, StringComparison.Ordinal);
    }

    private static ProjectZomboidDedicatedServerHostInputs Inputs(
        string launchPath = @"C:\PZ\StartServer64.bat",
        string workingDirectory = @"C:\PZ",
        string cacheDirectory = @"C:\Steward\workspaces\project-zomboid\abc\Zomboid",
        string serverName = "steward-test")
        => new(
            launchPath,
            workingDirectory,
            cacheDirectory,
            serverName,
            ["-cachedir=" + cacheDirectory, "-servername", serverName]);

    private string CreateOwnedCacheDirectory()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        var operationRoot = Path.Combine(
            localData,
            "Steward",
            "workspaces",
            "project-zomboid",
            Guid.NewGuid().ToString("N"));
        _ownedOperationRoots.Add(operationRoot);
        var cacheDirectory = Path.Combine(operationRoot, "Zomboid");
        Directory.CreateDirectory(cacheDirectory);
        return Path.GetFullPath(cacheDirectory);
    }

    public void Dispose()
    {
        foreach (var operationRoot in _ownedOperationRoots)
        {
            TryDeleteDirectory(operationRoot);
        }

        TryDeleteDirectory(_root);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
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
