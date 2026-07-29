using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private DependencyPropertyDescriptor? _statusTextDescriptor;
    private bool _quietStatusPresentationInitialized;

    internal void InitializeQuietStatusPresentation()
    {
        if (_quietStatusPresentationInitialized)
        {
            return;
        }

        _quietStatusPresentationInitialized = true;
        _statusTextDescriptor = DependencyPropertyDescriptor.FromProperty(
            TextBlock.TextProperty,
            typeof(TextBlock));
        _statusTextDescriptor?.AddValueChanged(StatusText, (_, _) => UpdateStatusBarVisibility());
        UpdateStatusBarVisibility();
    }

    private void UpdateStatusBarVisibility()
    {
        StatusBar.Visibility = IsRoutineStatus(StatusText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static bool IsRoutineStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status) ||
            string.Equals(status, DesktopText.Ready, StringComparison.Ordinal))
        {
            return true;
        }

        return status.Contains("managed World", StringComparison.Ordinal) ||
               status.Contains("supported games • no managed Worlds yet", StringComparison.Ordinal);
    }
}
