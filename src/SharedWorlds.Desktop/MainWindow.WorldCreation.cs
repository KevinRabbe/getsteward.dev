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
                "Steward could not attach Create World to the Games header.");
        }

        header.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(OpenImportButton, 2);
        Grid.SetColumn(RefreshButton, 3);

        var createButton = new Button
        {
            Content = "Create",
            Padding = new Thickness(12, 6, 12, 6),
            MinHeight = 32,
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(createButton, "Create new World");
        AutomationProperties.SetHelpText(
            createButton,
            "Create a new native game World through Steward.");
        Grid.SetColumn(createButton, 1);
        createButton.Click += CreateWorldButton_Click;
        header.Children.Add(createButton);
        _createWorldButton = createButton;
        UpdateNativeWorldCreationActionState();
    }

    private async void CreateWorldButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await RunUnifiedOperationAsync(
            "Checking installed games that can create Worlds...",
            async () =>
            {
                var options = new List<CreateWorldOption>();
                foreach (var adapter in _registeredGameAdapters.Values
                             .Where(adapter => adapter.Capabilities.HasFlag(
                                 GameAdapterCapabilities.NativeWorldCreation))
                             .OrderBy(adapter => adapter.DisplayName, StringComparer.OrdinalIgnoreCase))
                {
                    var installation = (await adapter.DiscoverInstallationsAsync()).FirstOrDefault();
                    if (installation is not null)
                    {
                        options.Add(new CreateWorldOption(adapter, installation));
                    }
                }

                if (options.Count == 0)
                {
                    StatusText.Text =
                        "No installed game currently has a Steward-validated Create World path.";
                    return;
                }

                var dialog = new CreateWorldDialog(options)
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
                StatusText.Text = $"Creating {worldName} with {dialog.SelectedOption.Adapter.DisplayName}...";

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

        _createWorldButton.IsEnabled = !_isBusy;
    }
}
