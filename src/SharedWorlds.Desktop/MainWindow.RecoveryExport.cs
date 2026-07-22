using System.IO;
using System.IO.Compression;
using Microsoft.Win32;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private async void ExportRecoveryCopyButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null)
        {
            return;
        }

        var snapshot = _responsibilityTracker.Current;
        if (snapshot.WorldId != world.Id ||
            snapshot.Kind is not (
                WorldLifecycleResponsibilityKind.InterruptedSession or
                WorldLifecycleResponsibilityKind.RecoveryNeeded or
                WorldLifecycleResponsibilityKind.CleanupPending))
        {
            StatusText.Text = "This World does not currently have an exportable recovery workspace.";
            return;
        }

        var record = (await _workspaceRecoveryStore.ListAsync())
            .Where(candidate => candidate.WorldId == world.Id)
            .OrderBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Id.ToString(), StringComparer.Ordinal)
            .FirstOrDefault();
        if (record is null)
        {
            StatusText.Text = "Steward could not find the durable recovery record for this World.";
            return;
        }

        var sourceDirectory = Path.GetFullPath(record.WorkingDirectory);
        if (!Directory.Exists(sourceDirectory))
        {
            StatusText.Text =
                "The recovery journal still exists, but its preserved workspace is no longer present on disk.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export Steward recovery copy",
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = BuildRecoveryExportFileName(world.Name, record.CreatedAt)
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var destinationPath = Path.GetFullPath(dialog.FileName);
        if (IsPathInsideDirectory(destinationPath, sourceDirectory))
        {
            StatusText.Text =
                "Choose an export location outside the preserved recovery workspace.";
            return;
        }

        await RunOperationAsync(
            $"Exporting recovery copy for {world.Name}...",
            async () =>
            {
                var destinationDirectory = Path.GetDirectoryName(destinationPath)
                    ?? throw new InvalidOperationException("Could not resolve the recovery export directory.");
                Directory.CreateDirectory(destinationDirectory);
                var temporaryPath = Path.Combine(
                    destinationDirectory,
                    $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

                try
                {
                    await Task.Run(() =>
                    {
                        ZipFile.CreateFromDirectory(
                            sourceDirectory,
                            temporaryPath,
                            CompressionLevel.Optimal,
                            includeBaseDirectory: false);
                        File.Move(temporaryPath, destinationPath, overwrite: true);
                    });
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                    catch (IOException)
                    {
                        // The completed/exported file is authoritative for this user action. A stale
                        // temporary file is harmless and must not change recovery or canonical state.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Same as above: never turn export cleanup into a World-state mutation.
                    }
                }

                StatusText.Text =
                    $"Recovery copy exported for '{world.Name}'. The canonical World and recovery journal were not changed.";
            });
    }

    private static string BuildRecoveryExportFileName(string worldName, DateTimeOffset createdAt)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safeName = new string(worldName
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "World";
        }

        return $"{safeName}-Steward-Recovery-{createdAt:yyyyMMdd-HHmmss}.zip";
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) +
                                  Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return normalizedPath.StartsWith(normalizedDirectory, comparison);
    }
}
