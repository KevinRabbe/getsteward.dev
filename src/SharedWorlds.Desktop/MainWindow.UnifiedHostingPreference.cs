using System.IO;
using System.Text.Json;
using System.Windows;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private bool _unifiedHostingPreferenceInitialized;

    internal void InitializeUnifiedHostingPreference()
    {
        if (_unifiedHostingPreferenceInitialized)
        {
            return;
        }

        _unifiedHostingPreferenceInitialized = true;
        AllowHostingCheckBox.Click -= AllowHostingCheckBox_Click;
        AllowHostingCheckBox.Click += UnifiedAllowHostingCheckBox_Click;
    }

    private async void UnifiedAllowHostingCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var previous = _deviceSettings;
        var updated = previous with
        {
            AllowHosting = AllowHostingCheckBox.IsChecked == true,
            HostingPreferenceExplicit = true
        };

        try
        {
            await _deviceSettingsStore.SaveAsync(updated);
            _deviceSettings = updated;
            StatusText.Text = updated.AllowHosting
                ? "This device is now eligible to host Worlds."
                : "Hosting is disabled on this device.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AllowHostingCheckBox.IsChecked = previous.AllowHosting;
            ShowError("Could not save device settings", exception);
        }

        UpdateHostingPreferenceText();
        UpdateUnifiedActionState();
    }
}
