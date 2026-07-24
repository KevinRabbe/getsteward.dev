using System.IO;
using System.Net.Http;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    internal async Task InitializeStewardRemoteSessionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!StewardDesktopRemoteConfiguration.TryLoadFromEnvironment(
                out var configuration,
                out var configurationProblem))
        {
            if (configurationProblem is not null)
            {
                StatusText.Text = $"Shared Worlds are disabled: {configurationProblem}";
            }

            return;
        }

        if (!_deviceSettingsUsableForRemote)
        {
            StatusText.Text =
                "Shared Worlds are disabled on this launch because Steward could not establish a durable installation identity. Local Worlds remain available.";
            return;
        }

        StatusText.Text = "Authenticating Steward with Steam...";
        try
        {
            if (!SteamWebApiTicketSource.TryCreate(
                    configuration!.SteamAppId,
                    out var ticketSource,
                    out var steamProblem))
            {
                StatusText.Text =
                    $"Shared Worlds are unavailable on this launch. {steamProblem} Local Worlds remain available.";
                return;
            }

            using var steamTickets = ticketSource!;
            using var ticket = await steamTickets.RequestAsync(
                configuration.SteamWebApiIdentity,
                cancellationToken);
            using var apiHandler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };
            using var apiClient = new HttpClient(apiHandler)
            {
                BaseAddress = configuration.ApiBaseAddress,
                Timeout = TimeSpan.FromSeconds(30)
            };

            var sessionClient = new StewardSessionClient(apiClient);
            var authentication = await sessionClient.AuthenticateSteamAsync(
                ticket.TicketHex,
                _deviceSettings.InstallationId,
                cancellationToken);
            if (authentication.Status != RemoteSteamAuthenticationStatus.Authenticated ||
                authentication.Tokens is null)
            {
                StatusText.Text =
                    "Steam authentication was rejected by Steward. Local Worlds remain available.";
                return;
            }

            await SetAuthenticatedRemoteRuntimeAsync(
                configuration.ApiBaseAddress,
                authentication.Tokens,
                ticket.User,
                cancellationToken);
            StatusText.Text = _lastRemoteWorldLoadError is null
                ? $"Shared Worlds connected as {ticket.User.DisplayName}."
                : "Steward authenticated successfully, but shared Worlds are temporarily unavailable. Local Worlds remain available.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            IOException or
            TimeoutException or
            InvalidOperationException)
        {
            StatusText.Text =
                $"Shared Worlds are temporarily unavailable: {exception.Message} Local Worlds remain available.";
        }
    }
}
