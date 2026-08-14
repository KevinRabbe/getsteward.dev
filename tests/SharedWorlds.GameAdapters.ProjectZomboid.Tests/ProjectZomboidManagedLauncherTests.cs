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
    private readonly List<string> _ownedWorkspaces = [];

    [Fact]
    public void TransformBindsServerRootCacheAndServerNameWithoutChangingOtherLines()
    {
        var inputs = Inputs(
            @"C:\Steam Library\Project Zomboid Dedicated Server\StartServer64.bat",
            @"C:\Steam Library\Project Zomboid Dedicated Server",
            @"C:\SafeWorld\prepared-workspace-scratch\project-zomboid\abc",
            "steward test");

        var result = ProjectZomboidManagedLauncherWriter.Transform(SourceBatch, inputs);

        Assert.Contains(
            "@cd /d \"C:\\Steam Library\\Project Zomboid Dedicated Server\"\r\n",
            result,
            StringComparison.Ordinal);
        Assert.Contains(
            "zombie.network.GameServer -statistic 0 \"-cachedir=C:\\SafeWorld\\prepared-workspace-scratch\\project-zomboid\\abc\" -servername \"steward test\"",
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
    public void MaterializeWritesManagedCopyInsideExplicitRuntimeNamespaceWithoutChangingSteamLauncher()
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
            Path.Combine(
                cacheDirectory,
                ProjectZomboidManagedLauncherWriter.RuntimeDirectoryName,
                "StartServer64.sharedworlds.bat"),
            launcher.Path);
        Assert.True(File.Exists(launcher.Path));
        Assert.Equal(SourceBatch, File.ReadAllText(sourcePath));
        var managedText = File.ReadAllText(launcher.Path);
        Assert.Contains("-cachedir=", managedText, StringComparison.Ordinal);
        Assert.Contains("-servername \"steward-test\"", managedText, StringComparison.Ordinal);
    }

    [Fact]
    public void MaterializeRejectsOversizedLauncherBeforeOutputCreation()
    {
        Directory.CreateDirectory(_root);
        var serverRoot = Path.Combine(_root, "oversized-server");
        Directory.CreateDirectory(serverRoot);
        var sourcePath = Path.Combine(serverRoot, "StartServer64.bat");
        using (var stream = new FileStream(
                   sourcePath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(ProjectZomboidManagedLauncherWriter.MaximumSourceLauncherBytes + 1L);
        }

        var cacheDirectory = CreateOwnedCacheDirectory();
        var inputs = Inputs(sourcePath, serverRoot, cacheDirectory, "steward-test");
        var managedPath = Path.Combine(
            cacheDirectory,
            ProjectZomboidManagedLauncherWriter.RuntimeDirectoryName,
            "StartServer64.sharedworlds.bat");

        var exception = Assert.Throws<InvalidDataException>(() =>
            ProjectZomboidManagedLauncherWriter.Materialize(inputs));

        Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(managedPath));
        Assert.Equal(
            ProjectZomboidManagedLauncherWriter.MaximumSourceLauncherBytes + 1L,
            new FileInfo(sourcePath).Length);
    }

    [Fact]
    public void MaterializeRejectsLauncherOutsideSuppliedServerRoot()
    {
        Directory.CreateDirectory(_root);
        var serverRoot = Path.Combine(_root, "server-root");
        Directory.CreateDirectory(serverRoot);
        var outsideRoot = Path.Combine(_root, "outside-root");
        Directory.CreateDirectory(outsideRoot);
        var sourcePath = Path.Combine(outsideRoot, "StartServer64.bat");
        File.WriteAllText(sourcePath, SourceBatch);
        var cacheDirectory = CreateOwnedCacheDirectory();
        var inputs = Inputs(sourcePath, serverRoot, cacheDirectory, "steward-test");
        var managedPath = Path.Combine(
            cacheDirectory,
            ProjectZomboidManagedLauncherWriter.RuntimeDirectoryName,
            "StartServer64.sharedworlds.bat");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidManagedLauncherWriter.Materialize(inputs));

        Assert.Contains("outside the supplied dedicated-server root", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(managedPath));
    }

    [Fact]
    public void MaterializeRejectsLinkedLauncherAtReadBoundary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var serverRoot = Path.Combine(_root, "linked-launcher-server");
        Directory.CreateDirectory(serverRoot);
        var outsidePath = Path.Combine(_root, "outside-launcher.bat");
        File.WriteAllText(outsidePath, SourceBatch);
        var sourcePath = Path.Combine(serverRoot, "StartServer64.bat");
        File.CreateSymbolicLink(sourcePath, outsidePath);
        var cacheDirectory = CreateOwnedCacheDirectory();
        var inputs = Inputs(sourcePath, serverRoot, cacheDirectory, "steward-test");
        var managedPath = Path.Combine(
            cacheDirectory,
            ProjectZomboidManagedLauncherWriter.RuntimeDirectoryName,
            "StartServer64.sharedworlds.bat");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidManagedLauncherWriter.Materialize(inputs));

        Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(managedPath));
        Assert.Equal(SourceBatch, File.ReadAllText(outsidePath));
    }

    private static ProjectZomboidDedicatedServerHostInputs Inputs(
        string launchPath = @"C:\PZ\StartServer64.bat",
        string workingDirectory = @"C:\PZ",
        string cacheDirectory = @"C:\SafeWorld\prepared-workspace-scratch\project-zomboid\abc",
        string serverName = "steward-test")
        => new(
            launchPath,
            workingDirectory,
            cacheDirectory,
            serverName,
            ["-cachedir=" + cacheDirectory, "-servername", serverName]);

    private string CreateOwnedCacheDirectory()
    {
        var workspace = ProjectZomboidWorkspaceOwnership.Create();
        _ownedWorkspaces.Add(workspace);
        return workspace;
    }

    public void Dispose()
    {
        foreach (var workspace in _ownedWorkspaces)
        {
            try
            {
                if (Directory.Exists(workspace))
                {
                    ProjectZomboidWorkspaceOwnership.DeleteOwned(workspace);
                }
            }
            catch
            {
            }
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
