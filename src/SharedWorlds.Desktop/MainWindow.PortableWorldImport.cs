using System.Globalization;
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
    private Button? _moreButton;
    private MenuItem? _openWorldFileMenuItem;
    private TextBlock? _worldOriginText;

    private void InitializePortableWorldImportUi()
    {
        if (_moreButton is not null)
        {
            return;
        }

        var topBar = FindSafeWorldTopBar();
        if (topBar is null)
        {
            return;
        }

        _openWorldFileMenuItem = new MenuItem
        {
            Header = DesktopText.OpenWorldFile
        };
        _openWorldFileMenuItem.Click += OpenWorldFileMenuItem_Click;

        var menu = new ContextMenu();
        menu.Items.Add(_openWorldFileMenuItem);

        _moreButton = new Button
        {
            Content = "⋯",
            Margin = new Thickness(12, 0, 0, 0),
            MinWidth = 38,
            Padding = new Thickness(8, 3, 8, 5),
            ToolTip = DesktopText.More,
            ContextMenu = menu,
            IsEnabled = RefreshButton.IsEnabled
        };
        AutomationProperties.SetName(_moreButton, DesktopText.More);
        AutomationProperties.SetHelpText(
            _moreButton,
            "Open less-frequent Safe World actions.");
        _moreButton.Click += MoreButton_Click;
        DockPanel.SetDock(_moreButton, Dock.Right);
        topBar.Children.Add(_moreButton);

        RefreshButton.IsEnabledChanged += (_, _) =>
        {
            if (_moreButton is not null)
            {
                _moreButton.IsEnabled = RefreshButton.IsEnabled;
            }
        };

        _worldOriginText = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 13,
            Foreground = GameText.Foreground,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var gameTextIndex = WorldDetailsPanel.Children.IndexOf(GameText);
        WorldDetailsPanel.Children.Insert(
            gameTextIndex >= 0 ? gameTextIndex + 1 : 0,
            _worldOriginText);
        WorldList.SelectionChanged += PortableWorldImportSelectionChanged;
        RefreshPortableWorldOriginPresentation();
    }

    private DockPanel? FindSafeWorldTopBar()
    {
        if (Content is not Grid root)
        {
            return null;
        }

        return root.Children
            .OfType<Border>()
            .Where(border => Grid.GetRow(border) == 0)
            .Select(border => border.Child)
            .OfType<DockPanel>()
            .FirstOrDefault();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _moreButton?.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = _moreButton;
        menu.IsOpen = true;
    }

    private async void OpenWorldFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = DesktopText.OpenWorldFile,
            Filter = "Safe World (*.safeworld)|*.safeworld",
            DefaultExt = ".safeworld",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var sourcePath = Path.GetFullPath(dialog.FileName);
        await RunUnifiedOperationAsync(
            $"Opening {Path.GetFileName(sourcePath)}...",
            () => ImportPortableWorldFileAsync(sourcePath));
    }

    private async Task ImportPortableWorldFileAsync(string sourcePath)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.RandomAccess);

        var preflight = await PortableWorldArchiveInspector.InspectAsync(source);
        if (!TryGetAdapter(preflight.GameAdapterId, out var adapter))
        {
            throw new NotSupportedException(
                $"This Safe World file belongs to unsupported game adapter '{preflight.GameAdapterId}'.");
        }

        // Presentation-time authority check avoids copying a large payload that the authoritative
        // import service will reject anyway. The service independently repeats this exact gate.
        if (adapter is not IPublicWorldExportAdapter publicWorldAdapter)
        {
            throw new NotSupportedException(
                $"{adapter.DisplayName} has not been qualified for public portable Worlds.");
        }

        var readiness = await publicWorldAdapter.CheckPublicWorldExportAsync(preflight.Environment);
        if (!readiness.IsSupported)
        {
            throw new NotSupportedException(
                readiness.Reason ??
                $"{adapter.DisplayName} has not been qualified for this portable World environment.");
        }

        var stagingRoot = GetPortableWorldImportStagingRoot();
        Directory.CreateDirectory(stagingRoot);
        EnsurePortableWorldImportDiskCapacity(stagingRoot, preflight.StateBytes);

        var stagingPath = Path.Combine(
            stagingRoot,
            $"{Guid.NewGuid():N}.state.tmp");
        try
        {
            await using var staging = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 128 * 1024,
                options: FileOptions.Asynchronous |
                         FileOptions.SequentialScan |
                         FileOptions.DeleteOnClose);

            source.Position = 0;
            var importer = new PortableWorldImportService(_storage);
            var result = await importer.ImportAsync(source, adapter, staging);

            StatusText.Text = $"Opened '{result.World.Name}' as an independent World.";
            await RefreshUnifiedWorldsAsync(result.World.Id, preserveStatus: true);
        }
        finally
        {
            TryDeletePortableWorldImportStaging(stagingPath);
        }
    }

    private static string GetPortableWorldImportStagingRoot()
    {
        var localDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localDataRoot, "SharedWorlds", "portable-import-staging");
    }

    private static void EnsurePortableWorldImportDiskCapacity(string stagingRoot, long stateBytes)
    {
        if (stateBytes < 0)
        {
            throw new InvalidDataException("Portable World state size cannot be negative.");
        }

        // At peak Safe World holds one fully verified staging copy while LocalWorldStorage writes its
        // atomic canonical payload copy on the same volume. The source .safeworld already exists and
        // therefore is not new disk consumption caused by this operation.
        var requiredFreeBytes = stateBytes > long.MaxValue / 2
            ? long.MaxValue
            : stateBytes * 2;
        var fullStagingRoot = Path.GetFullPath(stagingRoot);
        var driveRoot = Path.GetPathRoot(fullStagingRoot);
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            throw new IOException("Could not determine the Safe World storage volume.");
        }

        var drive = new DriveInfo(driveRoot);
        if (drive.AvailableFreeSpace >= requiredFreeBytes)
        {
            return;
        }

        throw new IOException(
            $"Opening this World needs about {FormatByteCount(requiredFreeBytes)} of free space on " +
            $"{drive.Name}; {FormatByteCount(drive.AvailableFreeSpace)} is available.");
    }

    private static string FormatByteCount(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", value, units[unit]);
    }

    private static void TryDeletePortableWorldImportStaging(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // FileOptions.DeleteOnClose is the primary cleanup path. A best-effort fallback must not
            // replace the user-visible import result with a secondary cleanup failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale as above; the file was opened with DeleteOnClose before import started.
        }
    }

    private void PortableWorldImportSelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshPortableWorldOriginPresentation();

    private void RefreshPortableWorldOriginPresentation()
    {
        if (_worldOriginText is null)
        {
            return;
        }

        var origin = _selectedWorld?.StartedFrom;
        if (origin is null)
        {
            _worldOriginText.Text = string.Empty;
            _worldOriginText.Visibility = Visibility.Collapsed;
            return;
        }

        _worldOriginText.Text = string.IsNullOrWhiteSpace(origin.Creator)
            ? string.Format(
                CultureInfo.CurrentCulture,
                DesktopText.StartedFromFormat,
                origin.WorldName)
            : string.Format(
                CultureInfo.CurrentCulture,
                DesktopText.StartedFromCreatorFormat,
                origin.WorldName,
                origin.Creator);
        _worldOriginText.Visibility = Visibility.Visible;
    }
}
