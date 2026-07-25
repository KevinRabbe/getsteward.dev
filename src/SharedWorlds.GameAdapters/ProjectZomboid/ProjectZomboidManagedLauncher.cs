using System.Text;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal sealed record ProjectZomboidManagedLauncher(
    string Path,
    string WorkingDirectory);

internal static class ProjectZomboidManagedLauncherWriter
{
    private const string ManagedLauncherFileName = "StartServer64.sharedworlds.bat";
    private const string QuotedSourceDirectoryToken = "\"%~dp0\"";
    private const string SourceDirectoryToken = "%~dp0";
    private const string ForwardedArgumentsToken = "%1 %2";
    private const string GameServerMarker = "zombie.network.GameServer";

    internal static ProjectZomboidManagedLauncher Materialize(
        ProjectZomboidDedicatedServerHostInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ProjectZomboidWorkspaceOwnership.RequireOwned(inputs.CacheDirectory);

        var operationRoot = Directory.GetParent(inputs.CacheDirectory)?.FullName
            ?? throw new InvalidOperationException(
                "Could not determine the Steward Project Zomboid operation root.");
        var managedPath = Path.Combine(operationRoot, ManagedLauncherFileName);
        var sourceText = File.ReadAllText(inputs.LaunchPath);
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
