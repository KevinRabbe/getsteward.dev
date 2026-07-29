using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private Button? _createWorldButton;

    private void InitializeNativeWorldCreationUi()
    {
        if (_createWorldButton is not null)
        {
            return;
        }

        if (OpenImportButton.Parent is not Grid header)
        {
            throw new InvalidOperationException(
                "Safe World could not attach Create World to the Games header.");
        }

        header.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(OpenImportButton, 2);
        Grid.SetColumn(RefreshButton, 3);

        var createButton = new Button
        {
            Content = DesktopText.CreateWorld,
            Padding = new Thickness(12, 6, 12, 6),
            MinHeight = 32,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(createButton, DesktopText.CreateWorld);
        AutomationProperties.SetHelpText(
            createButton,
            "Create a new World for the selected game.");
        Grid.SetColumn(createButton, 1);
        createButton.Click += CreateWorldButton_Click;
        header.Children.Add(createButton);
        _createWorldButton = createButton;
        UpdateNativeWorldCreationActionState();
    }

    private async void CreateWorldButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy ||
            _selectedGameAdapterId is not { } adapterId ||
            !TryGetAdapter(adapterId, out var adapter) ||
            !adapter.Capabilities.HasFlag(GameAdapterCapabilities.NativeWorldCreation))
        {
            return;
        }

        await RunUnifiedOperationAsync(
            $"Checking {adapter.DisplayName}...",
            async () =>
            {
                var installation = (await adapter.DiscoverInstallationsAsync()).FirstOrDefault();
                if (installation is null)
                {
                    StatusText.Text = $"{adapter.DisplayName} is not installed on this device.";
                    return;
                }

                var option = new CreateWorldOption(adapter, installation);
                var dialog = new CreateWorldDialog([option])
                {
                    Owner = this
                };
                if (dialog.ShowDialog() != true ||
                    dialog.SelectedOption is null ||
                    string.IsNullOrWhiteSpace(dialog.WorldName))
                {
                    StatusText.Text = "Create World cancelled.";
                    return;
                }

                var worldName = await CreateUniqueWorldNameAsync(dialog.WorldName);
                StatusText.Text = $"Creating {worldName}...";

                var creation = new WorldCreationService(_storage);
                var world = await creation.CreateAsync(
                    dialog.SelectedOption.Adapter,
                    dialog.SelectedOption.Installation,
                    new WorldCreationRequest(worldName),
                    GetLocalUser());

                await TryEnableCreatorDeviceHostingAsync();
                StatusText.Text = $"Created '{world.Name}'.";
                await RefreshUnifiedWorldsAsync(world.Id, preserveStatus: true);
            });
    }

    private void UpdateNativeWorldCreationActionState()
    {
        if (_createWorldButton is null)
        {
            return;
        }

        var supported = _selectedGameAdapterId is { } adapterId &&
                        TryGetAdapter(adapterId, out var adapter) &&
                        adapter.Capabilities.HasFlag(GameAdapterCapabilities.NativeWorldCreation);
        _createWorldButton.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        _createWorldButton.IsEnabled = !_isBusy && supported;
    }
}
