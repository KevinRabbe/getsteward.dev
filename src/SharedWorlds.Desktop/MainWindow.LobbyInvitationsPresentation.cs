using System.Windows;
using System.Windows.Controls;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    internal void RehomeInvitationsToGlobalLobby()
    {
        if (_globalLobbyPanel?.Child is not ScrollViewer scroll ||
            scroll.Content is not StackPanel content)
        {
            return;
        }

        DetachSafeWorldSidebarElement(_invitationsButton);
        _invitationsButton.Width = 112;
        _invitationsButton.Height = 40;
        _invitationsButton.MinWidth = 0;
        _invitationsButton.MinHeight = 0;
        _invitationsButton.Margin = new Thickness(16, 0, 0, 0);
        _invitationsButton.VerticalAlignment = VerticalAlignment.Top;

        var title = content.Children
            .OfType<TextBlock>()
            .FirstOrDefault(text => string.Equals(text.Text, DesktopText.Lobby, StringComparison.Ordinal));
        if (title is null)
        {
            return;
        }

        var titleIndex = content.Children.IndexOf(title);
        content.Children.Remove(title);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(_invitationsButton, 1);
        header.Children.Add(title);
        header.Children.Add(_invitationsButton);
        content.Children.Insert(titleIndex, header);
    }
}
