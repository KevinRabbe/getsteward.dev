using System.Text;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidRuntimeManagementConfigurationTests : IDisposable
{
    private const string Password = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private readonly List<string> _operationRoots = [];
    private readonly string _cleanupRoot = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-pz-runtime-config-{Guid.NewGuid():N}");

    [Fact]
    public async Task MaterializeWritesOnlyTransientManagementCredential()
    {
        var inputs = CreateInputs();
        var configurationPath = CreateConfiguration(
            inputs,
            "PublicName=Original\r\n" +
            "Password=join-secret\r\n" +
            "RCONPort=27015\r\n" +
            "RCONPassword=\r\n" +
            "DiscordToken=\r\n");

        var runtime = await ProjectZomboidRuntimeManagementConfigurationWriter.MaterializeAsync(
            inputs,
            Password,
            CancellationToken.None);
        var text = await File.ReadAllTextAsync(configurationPath);

        Assert.Equal(configurationPath, runtime.ConfigurationPath);
        Assert.Equal(27015, runtime.Port);
        Assert.Equal(Password, runtime.Password);
        Assert.Contains("Password=join-secret\r\n", text, StringComparison.Ordinal);
        Assert.Contains($"RCONPassword={Password}\r\n", text, StringComparison.Ordinal);
        Assert.Contains("DiscordToken=\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, runtime.ToString(), StringComparison.Ordinal);
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(configurationPath)!));
    }

    [Fact]
    public async Task ScrubPreservesNewerNonSecretSettingsInsteadOfRestoringOldBytes()
    {
        var inputs = CreateInputs();
        var configurationPath = CreateConfiguration(
            inputs,
            "PublicName=Original\n" +
            "RCONPort=27015\n" +
            "RCONPassword=\n" +
            "DiscordToken=\n");

        await ProjectZomboidRuntimeManagementConfigurationWriter.MaterializeAsync(
            inputs,
            Password,
            CancellationToken.None);
        var runtimeText = await File.ReadAllTextAsync(configurationPath);
        runtimeText = runtimeText.Replace(
            "PublicName=Original",
            "PublicName=ChangedAfterMaterialization",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(configurationPath, runtimeText);

        await ProjectZomboidRuntimeManagementConfigurationWriter.ScrubAsync(
            inputs,
            CancellationToken.None);
        var scrubbed = await File.ReadAllTextAsync(configurationPath);

        Assert.Contains("PublicName=ChangedAfterMaterialization\n", scrubbed, StringComparison.Ordinal);
        Assert.Contains("RCONPort=27015\n", scrubbed, StringComparison.Ordinal);
        Assert.Contains("RCONPassword=\n", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedConfigurationIsRejectedBeforeReadOrReplacement()
    {
        var inputs = CreateInputs();
        var configurationPath = GetConfigurationPath(inputs);
        Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
        await using (var stream = new FileStream(
                         configurationPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {
            stream.SetLength(
                ProjectZomboidTransientManagementConfigurationBuilder.MaximumConfigurationBytes + 1L);
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProjectZomboidRuntimeManagementConfigurationWriter.MaterializeAsync(
                inputs,
                Password,
                CancellationToken.None));

        Assert.Contains("management safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ProjectZomboidTransientManagementConfigurationBuilder.MaximumConfigurationBytes + 1L,
            new FileInfo(configurationPath).Length);
    }

    [Fact]
    public async Task LinkedServerDirectoryIsRejectedBeforeExternalConfigurationWrite()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var inputs = CreateInputs();
        var outside = Path.Combine(_cleanupRoot, Guid.NewGuid().ToString("N"), "outside-server");
        Directory.CreateDirectory(outside);
        var outsideConfiguration = Path.Combine(outside, inputs.ServerName + ".ini");
        const string source = "RCONPort=27015\nRCONPassword=\n";
        await File.WriteAllTextAsync(outsideConfiguration, source);

        var serverDirectory = Path.Combine(inputs.CacheDirectory, "Server");
        Directory.CreateSymbolicLink(serverDirectory, outside);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProjectZomboidRuntimeManagementConfigurationWriter.MaterializeAsync(
                    inputs,
                    Password,
                    CancellationToken.None));

            Assert.Contains("linked/reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(source, await File.ReadAllTextAsync(outsideConfiguration));
        }
        finally
        {
            Directory.Delete(serverDirectory);
        }
    }

    private ProjectZomboidDedicatedServerHostInputs CreateInputs()
    {
        var operationRoot = Path.Combine(
            GetExpectedWorkRoot(),
            Guid.NewGuid().ToString("N"));
        _operationRoots.Add(operationRoot);
        var workingDirectory = Path.Combine(operationRoot, "Zomboid");
        Directory.CreateDirectory(workingDirectory);
        return new ProjectZomboidDedicatedServerHostInputs(
            LaunchPath: Path.Combine(Path.GetTempPath(), "unused-StartServer64.bat"),
            WorkingDirectory: Path.GetTempPath(),
            CacheDirectory: workingDirectory,
            ServerName: "servertest",
            GameArguments: []);
    }

    private static string CreateConfiguration(
        ProjectZomboidDedicatedServerHostInputs inputs,
        string text)
    {
        var path = GetConfigurationPath(inputs);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string GetConfigurationPath(ProjectZomboidDedicatedServerHostInputs inputs)
        => Path.GetFullPath(Path.Combine(
            inputs.CacheDirectory,
            "Server",
            inputs.ServerName + ".ini"));

    private static string GetExpectedWorkRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        return Path.Combine(localData, "Steward", "workspaces", "project-zomboid");
    }

    public void Dispose()
    {
        foreach (var operationRoot in _operationRoots)
        {
            try
            {
                if (Directory.Exists(operationRoot))
                {
                    Directory.Delete(operationRoot, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        try
        {
            if (Directory.Exists(_cleanupRoot))
            {
                Directory.Delete(_cleanupRoot, recursive: true);
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
