using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Infrastructure.Portability;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private Button? _shareCopyButton;

    private void InitializePortableWorldExportUi()
    {
        if (_shareCopyButton is not null || ShareButton.Parent is not Panel actions)
        {
            return;
        }

        _shareCopyButton = new Button
        {
            Content = DesktopText.ShareCopy,
            Margin = new Thickness(0, 0, 10, 10),
            Visibility = Visibility.Collapsed,
            IsEnabled = false
        };
        AutomationProperties.SetName(_shareCopyButton, DesktopText.ShareCopy);
        AutomationProperties.SetHelpText(
            _shareCopyButton,
            "Create an independent portable copy of the current saved World that another Safe World user can open.");
        _shareCopyButton.Click += ShareCopyButton_Click;
        actions.Children.Add(_shareCopyButton);

        WorldList.SelectionChanged += PortableWorldExportSelectionChanged;
    }

    private async void PortableWorldExportSelectionChanged(object sender, SelectionChangedEventArgs e)
        => await RefreshPortableWorldExportActionAsync();

    private async Task RefreshPortableWorldExportActionAsync()
    {
        if (_shareCopyButton is null)
        {
            return;
        }

        var world = _selectedWorld;
        if (world is null ||
            world.CurrentEnvironmentRevisionId is not { } environmentRevisionId ||
            world.CurrentStateRevisionId is null ||
            !TryGetAdapter(world.GameAdapterId, out var adapter) ||
            adapter is not IPublicWorldExportAdapter publicExportAdapter)
        {
            _shareCopyButton.Visibility = Visibility.Collapsed;
            _shareCopyButton.IsEnabled = false;
            return;
        }

        try
        {
            var environment = await LoadEnvironmentRevisionForWorldAsync(world, environmentRevisionId);
            if (environment is null)
            {
                _shareCopyButton.Visibility = Visibility.Collapsed;
                _shareCopyButton.IsEnabled = false;
                return;
            }

            var readiness = await publicExportAdapter.CheckPublicWorldExportAsync(environment.Manifest);
            if (_selectedWorld?.Id != world.Id)
            {
                return;
            }

            _shareCopyButton.Visibility = readiness.IsSupported
                ? Visibility.Visible
                : Visibility.Collapsed;
            _shareCopyButton.IsEnabled = readiness.IsSupported && !_isBusy;
        }
        catch
        {
            // Public export is optional presentation. Unexpected readiness failures fail closed here;
            // the authoritative export service independently rechecks before writing any artifact.
            if (_selectedWorld?.Id == world.Id)
            {
                _shareCopyButton.Visibility = Visibility.Collapsed;
                _shareCopyButton.IsEnabled = false;
            }
        }
    }

    private void UpdatePortableWorldExportBusyState()
    {
        if (_shareCopyButton is not null)
        {
            _shareCopyButton.IsEnabled =
                _shareCopyButton.Visibility == Visibility.Visible && !_isBusy;
        }
    }

    private async void ShareCopyButton_Click(object sender, RoutedEventArgs e)
    {
        var world = _selectedWorld;
        if (world is null ||
            !TryGetAdapter(world.GameAdapterId, out var adapter) ||
            adapter is not IPublicWorldExportAdapter)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = $"Share a Copy of {world.Name}",
            Filter = "Safe World (*.safeworld)|*.safeworld",
            DefaultExt = ".safeworld",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = CreatePortableWorldFileName(world.Name) + ".safeworld"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var targetPath = Path.GetFullPath(dialog.FileName);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("Could not resolve the selected export directory.");
        Directory.CreateDirectory(targetDirectory);
        var temporaryPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await RunOperationAsync(
                $"Creating {Path.GetFileName(targetPath)}...",
                async () =>
                {
                    var exporter = new PortableWorldExportService(_storage);
                    {
                        await using var output = new FileStream(
                            temporaryPath,
                            FileMode.CreateNew,
                            FileAccess.ReadWrite,
                            FileShare.None,
                            bufferSize: 128 * 1024,
                            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await exporter.ExportCurrentAsync(world, adapter, output);
                        await output.FlushAsync();
                        output.Flush(flushToDisk: true);
                    }

                    PublishPortableWorldFile(temporaryPath, targetPath);
                    StatusText.Text = $"Created '{Path.GetFileName(targetPath)}'.";
                });
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void PublishPortableWorldFile(string temporaryPath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            File.Replace(
                temporaryPath,
                targetPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            return;
        }

        File.Move(temporaryPath, targetPath);
    }

    private static string CreatePortableWorldFileName(string worldName)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(worldName
            .Trim()
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "World" : cleaned;
    }
}
