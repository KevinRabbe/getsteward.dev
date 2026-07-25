using System.Text;
using System.Text.RegularExpressions;

namespace SharedWorlds.GameAdapters.ConanExilesEnhanced;

internal static partial class ConanExilesEnhancedSteamManifest
{
    internal const long MaximumBytes = 4L * 1024 * 1024;

    internal static ConanExilesEnhancedSteamManifestRecord ReadRequired(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var fullPath = Path.GetFullPath(manifestPath);
        using var stream = OpenRegularBounded(fullPath);
        var maximum = checked((int)MaximumBytes);
        var bytes = new byte[maximum + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > maximum)
        {
            throw new InvalidOperationException(
                $"Conan Exiles Enhanced's Steam manifest exceeded Steward's {MaximumBytes}-byte metadata safety limit while being read: {fullPath}");
        }

        var text = Encoding.UTF8.GetString(bytes, 0, total);
        var buildMatch = BuildIdRegex().Match(text);
        var installDirMatch = InstallDirRegex().Match(text);
        if (!buildMatch.Success || string.IsNullOrWhiteSpace(buildMatch.Groups[1].Value))
        {
            throw new InvalidOperationException(
                $"Steam buildid was not found in Conan Exiles Enhanced's manifest: {fullPath}");
        }

        if (!installDirMatch.Success || string.IsNullOrWhiteSpace(installDirMatch.Groups[1].Value))
        {
            throw new InvalidOperationException(
                $"Steam installdir was not found in Conan Exiles Enhanced's manifest: {fullPath}");
        }

        var installDir = installDirMatch.Groups[1].Value;
        if (installDir.IndexOfAny(['/', '\\']) >= 0 ||
            installDir is "." or ".." ||
            Path.IsPathRooted(installDir))
        {
            throw new InvalidOperationException(
                $"Conan Exiles Enhanced's Steam installdir is not a safe library-relative directory name: {fullPath}");
        }

        return new ConanExilesEnhancedSteamManifestRecord(
            buildMatch.Groups[1].Value,
            installDir);
    }

    private static FileStream OpenRegularBounded(string fullPath)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Conan Exiles Enhanced's Steam manifest could not be opened safely: {fullPath}",
                exception);
        }

        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Conan Exiles Enhanced's Steam manifest is not a regular owned file: {fullPath}");
            }

            if (stream.Length > MaximumBytes)
            {
                throw new InvalidOperationException(
                    $"Conan Exiles Enhanced's Steam manifest exceeds Steward's {MaximumBytes}-byte metadata safety limit: {fullPath}");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildIdRegex();

    [GeneratedRegex("\\\"installdir\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallDirRegex();
}

internal sealed record ConanExilesEnhancedSteamManifestRecord(
    string BuildId,
    string InstallDirectoryName);
