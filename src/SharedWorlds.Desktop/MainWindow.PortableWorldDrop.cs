using System.IO;
using System.Windows;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private void InitializePortableWorldDropUi()
    {
        AllowDrop = true;
        PreviewDragOver += PortableWorldPreviewDragOver;
        PreviewDrop += PortableWorldPreviewDrop;
    }

    private void PortableWorldPreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_isBusy && TryResolveDroppedPortableWorld(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void PortableWorldPreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_isBusy || !TryResolveDroppedPortableWorld(e.Data, out var sourcePath))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        await OpenPortableWorldFromPathAsync(sourcePath);
    }

    private static bool TryResolveDroppedPortableWorld(
        IDataObject data,
        out string sourcePath)
    {
        sourcePath = string.Empty;
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return false;
        }

        var resolved = PortableWorldDropActivation.ResolvePath(paths);
        if (resolved is null || !File.Exists(resolved))
        {
            return false;
        }

        sourcePath = resolved;
        return true;
    }
}
