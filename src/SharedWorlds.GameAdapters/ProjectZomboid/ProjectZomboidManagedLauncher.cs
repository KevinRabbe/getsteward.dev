using System.Text;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal sealed record ProjectZomboidManagedLauncher(
    string Path,
    string WorkingDirectory);

internal static class ProjectZomboidManagedLauncherWriter
{
    internal const long MaximumSourceLauncherBytes = 4L * 1024 * 1024;
    internal const string RuntimeDirectoryName = ".safeworld-runtime";
    private const string ManagedLauncherFileName = "StartServer64.sharedworlds.bat";
    private const string QuotedSourceDirectoryToken = "\"%~dp0\"";
    private const string SourceDirectoryToken = "%~dp0";
    private const string ForwardedArgumentsToken = "%1 %2";
    private const string GameServerMarker = "zombie.network.GameServer";

    internal static ProjectZomboidManagedLauncher Materialize(
        ProjectZomboidDedicatedServerHostInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var workspace = ProjectZomboidWorkspaceOwnership.RequireOwned(inputs.CacheDirectory);
        PreflightSourceLauncher(inputs);

        var runtimeRoot = ProjectZomboidWorkspaceOwnership.RequireOwnedPath(
            workspace,
            Path.Combine(workspace, RuntimeDirectoryName),
            "runtime directory");
        Directory.CreateDirectory(runtimeRoot);
        _ = ProjectZomboidWorkspaceOwnership.RequireOwnedPath(
            workspace,
            runtimeRoot,
            "runtime directory");

        var managedPath = ProjectZomboidWorkspaceOwnership.RequireOwnedPath(
            workspace,
            Path.Combine(runtimeRoot, ManagedLauncherFileName),
            "managed launcher");
        var sourceText = File.ReadAllText(Path.GetFullPath(inputs.LaunchPath));
        var managedText = Transform(sourceText, inputs);

        using (var stream = new FileStream(
                   managedPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        using (var writer = new StreamWriter(
                   stream,
                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(managedText);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        return new ProjectZomboidManagedLauncher(
            managedPath,
            inputs.WorkingDirectory);
    }

    internal static string Transform(
        string sourceText,
        ProjectZomboidDedicatedServerHostInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(inputs);

        var serverRoot = ValidateBatchValue(inputs.WorkingDirectory, "dedicated-server root");
        var cacheArgumentLiteral = QuoteBatchValue(
            "-cachedir=" + inputs.CacheDirectory,
            "cache directory");
        var serverNameLiteral = QuoteBatchValue(inputs.ServerName, "server name");

        var newline = sourceText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hadTrailingNewline = sourceText.EndsWith("\r\n", StringComparison.Ordinal) ||
                                 sourceText.EndsWith("\n", StringComparison.Ordinal);
        var lines = sourceText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        if (hadTrailingNewline && lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        var sourceDirectoryLineIndexes = lines
            .Select((line, index) => (line, index))
            .Where(item => item.line.Contains(QuotedSourceDirectoryToken, StringComparison.OrdinalIgnoreCase) &&
                           item.line.Contains("cd", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        if (sourceDirectoryLineIndexes.Length != 1)
        {
            throw new InvalidOperationException(
                $"Project Zomboid launcher must contain exactly one quoted '{SourceDirectoryToken}' working-directory line; found {sourceDirectoryLineIndexes.Length}.");
        }

        var gameServerLineIndexes = lines
            .Select((line, index) => (line, index))
            .Where(item => item.line.Contains(GameServerMarker, StringComparison.Ordinal))
            .Select(item => item.index)
            .ToArray();
        if (gameServerLineIndexes.Length != 1)
        {
            throw new InvalidOperationException(
                $"Project Zomboid launcher must contain exactly one {GameServerMarker} invocation; found {gameServerLineIndexes.Length}.");
        }

        var gameServerIndex = gameServerLineIndexes[0];
        var gameServerLine = lines[gameServerIndex];
        if (CountOccurrences(gameServerLine, ForwardedArgumentsToken) != 1)
        {
            throw new InvalidOperationException(
                $"Project Zomboid launcher must contain exactly one '{ForwardedArgumentsToken}' token on its GameServer invocation.");
        }

        if (gameServerLine.Contains("-cachedir", StringComparison.OrdinalIgnoreCase) ||
            gameServerLine.Contains("-servername", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Project Zomboid source launcher already contains Steward-owned game arguments. Steward will not guess how to merge them.");
        }

        var sourceDirectoryIndex = sourceDirectoryLineIndexes[0];
        lines[sourceDirectoryIndex] = lines[sourceDirectoryIndex].Replace(
            SourceDirectoryToken,
            serverRoot,
            StringComparison.OrdinalIgnoreCase);

        lines[gameServerIndex] = gameServerLine.Replace(
            ForwardedArgumentsToken,
            $"{cacheArgumentLiteral} -servername {serverNameLiteral}",
            StringComparison.Ordinal);

        var result = string.Join(newline, lines);
        return hadTrailingNewline ? result + newline : result;
    }

    private static void PreflightSourceLauncher(ProjectZomboidDedicatedServerHostInputs inputs)
    {
        var workingDirectory = Path.GetFullPath(inputs.WorkingDirectory);
        var launchPath = Path.GetFullPath(inputs.LaunchPath);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Project Zomboid dedicated-server root does not exist: {workingDirectory}");
        }

        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(workingDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not inspect Project Zomboid dedicated-server root '{workingDirectory}'.",
                exception);
        }

        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Project Zomboid dedicated-server root is linked or a reparse point: {workingDirectory}");
        }

        var launchDirectory = Path.GetDirectoryName(launchPath);
        if (launchDirectory is null || !PathsEqual(launchDirectory, workingDirectory))
        {
            throw new InvalidOperationException(
                "Project Zomboid Dedicated Server launcher is outside the supplied dedicated-server root.");
        }

        var file = new FileInfo(launchPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException(
                "Project Zomboid Dedicated Server launcher does not exist.",
                launchPath);
        }

        FileAttributes launcherAttributes;
        try
        {
            launcherAttributes = File.GetAttributes(launchPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not inspect Project Zomboid Dedicated Server launcher '{launchPath}'.",
                exception);
        }

        if ((launcherAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Project Zomboid Dedicated Server launcher is linked or a reparse point: {launchPath}");
        }

        if (file.Length > MaximumSourceLauncherBytes)
        {
            throw new InvalidDataException(
                $"Project Zomboid Dedicated Server launcher exceeds Steward's {MaximumSourceLauncherBytes}-byte safety limit.");
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string QuoteBatchValue(string value, string description)
        => '"' + ValidateBatchValue(value, description) + '"';

    private static string ValidateBatchValue(string value, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.IndexOfAny(['"', '%', '!', '\r', '\n']) >= 0)
        {
            throw new InvalidOperationException(
                $"Project Zomboid {description} contains characters Steward will not embed in a Windows batch launcher.");
        }

        return value;
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var offset = 0;
        while (true)
        {
            var index = value.IndexOf(token, offset, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            offset = index + token.Length;
        }
    }
}