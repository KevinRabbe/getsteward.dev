using System.Windows;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private const double NarrowWorkspaceWidth = 900;
    private bool _responsiveWorkspaceInitialized;

    internal void InitializeResponsiveWorkspace()
    {
        if (_responsiveWorkspaceInitialized)
        {
            return;
        }

        _responsiveWorkspaceInitialized = true;
        SizeChanged += (_, _) => UpdateResponsiveWorkspace();
        WorldList.SelectionChanged += (_, _) => UpdateResponsiveWorkspace();
        BackToWorldsButton.Click += (_, _) =>
        {
            WorldList.SelectedItem = null;
            WorldList.Focus();
            UpdateResponsiveWorkspace();
        };

        UpdateResponsiveWorkspace();
    }

    private void UpdateResponsiveWorkspace()
    {
        if (!_responsiveWorkspaceInitialized)
        {
            return;
        }

        var narrow = ActualWidth < NarrowWorkspaceWidth;
        if (!narrow)
        {
            WorldNavigationColumn.Width = new GridLength(330);
            WorldDetailsColumn.Width = new GridLength(1, GridUnitType.Star);
            WorldSidebar.Visibility = Visibility.Visible;
            WorldDetailsScroll.Visibility = Visibility.Visible;
            BackToWorldsButton.Visibility = Visibility.Collapsed;
            return;
        }

        var worldSelected = _selectedWorld is not null;
        if (worldSelected)
        {
            var navigationHadKeyboardFocus = WorldSidebar.IsKeyboardFocusWithin;

            WorldNavigationColumn.Width = new GridLength(0);
            WorldDetailsColumn.Width = new GridLength(1, GridUnitType.Star);
            WorldSidebar.Visibility = Visibility.Collapsed;
            WorldDetailsScroll.Visibility = Visibility.Visible;
            BackToWorldsButton.Visibility = Visibility.Visible;

            if (navigationHadKeyboardFocus)
            {
                BackToWorldsButton.Focus();
            }

            return;
        }

        WorldNavigationColumn.Width = new GridLength(1, GridUnitType.Star);
        WorldDetailsColumn.Width = new GridLength(0);
        WorldSidebar.Visibility = Visibility.Visible;
        WorldDetailsScroll.Visibility = Visibility.Collapsed;
        BackToWorldsButton.Visibility = Visibility.Collapsed;
    }
}
