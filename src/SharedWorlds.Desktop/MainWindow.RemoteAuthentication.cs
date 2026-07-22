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

        if (!SteamWebApiTicketSource.TryCreate(
                configuration!.SteamAppId,
                out var ticketSource,
                out var steamProblem))
        {
            StatusText.Text =
                $"Shared Worlds are unavailable on this launch. {steamProblem} Local Worlds remain available.";
            return;
        }

        StatusText.Text = "Authenticating Steward with Steam...";
        try
        {
            using var steamTickets = ticketSource!;
            using var ticket = await steamTickets.RequestAsync(
                configuration.SteamWebApiIdentity,
                cancellationToken);
            using var apiClient = new HttpClient
            {
                BaseAddress = NormalizeBaseAddress(configuration.ApiBaseAddress),
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
            StatusText.Text = $"Shared Worlds connected as {ticket.User.DisplayName}.";
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

    private static Uri NormalizeBaseAddress(Uri address)
    {
        var absolute = address.AbsoluteUri;
        return absolute.EndsWith("/", StringComparison.Ordinal)
            ? address
            : new Uri(absolute + '/', UriKind.Absolute);
    }
}
