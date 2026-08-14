using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    internal async Task InitializeStewardRemoteSessionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!StewardDesktopRemoteConfiguration.TryLoad(
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

        try
        {
            using var apiHandler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };
            using var apiClient = new HttpClient(apiHandler)
            {
                BaseAddress = configuration!.ApiBaseAddress,
                Timeout = TimeSpan.FromSeconds(30)
            };
            var sessionClient = new StewardSessionClient(apiClient);

            if (configuration.AuthenticationMode == StewardDesktopAuthenticationMode.FriendsBuild)
            {
                await AuthenticateFriendsBuildAsync(
                    configuration,
                    sessionClient,
                    cancellationToken);
                return;
            }

            await AuthenticateSteamAsync(
                configuration,
                sessionClient,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            IOException or
            TimeoutException or
            InvalidOperationException or
            UnauthorizedAccessException or
            CryptographicException)
        {
            StatusText.Text =
                $"Shared Worlds are temporarily unavailable: {exception.Message} Local Worlds remain available.";
        }
    }

    private async Task AuthenticateSteamAsync(
        StewardDesktopRemoteConfiguration configuration,
        StewardSessionClient sessionClient,
        CancellationToken cancellationToken)
    {
        var steamAppId = configuration.SteamAppId
            ?? throw new InvalidOperationException("Steam authentication mode is missing its AppID.");
        var steamIdentity = configuration.SteamWebApiIdentity
            ?? throw new InvalidOperationException("Steam authentication mode is missing its Web API identity.");

        StatusText.Text = "Authenticating Steward with Steam...";
        if (System.Windows.Application.Current is not App app)
        {
            StatusText.Text =
                "Shared Worlds are unavailable on this launch. Steam platform runtime is unavailable. Local Worlds remain available.";
            return;
        }

        if (!app.TryGetOrCreateSteamPlatformRuntime(
                steamAppId,
                out var steamPlatform,
                out var steamProblem))
        {
            StatusText.Text =
                $"Shared Worlds are unavailable on this launch. {steamProblem ?? "Steam platform runtime is unavailable."} Local Worlds remain available.";
            return;
        }

        using var steamTickets = new SteamWebApiTicketSource(steamPlatform!);
        using var ticket = await steamTickets.RequestAsync(
            steamIdentity,
            cancellationToken);
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

    private async Task AuthenticateFriendsBuildAsync(
        StewardDesktopRemoteConfiguration configuration,
        StewardSessionClient sessionClient,
        CancellationToken cancellationToken)
    {
        var credentialStore = new FriendsBuildCredentialStore(Path.Combine(
            DesktopLocalDataRoot.RequireResolvedRoot(),
            "settings",
            "friends-build-credential.bin"));

        string? credential;
        var credentialWasStored = false;
        try
        {
            credential = await credentialStore.LoadAsync(cancellationToken);
            credentialWasStored = credential is not null;
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidDataException)
        {
            credentialStore.Delete();
            credential = null;
        }

        string? promptMessage = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (credential is null)
            {
                credential = PromptForFriendsBuildCredential(promptMessage);
                credentialWasStored = false;
                if (credential is null)
                {
                    StatusText.Text = "Shared Worlds are not connected. Local Worlds remain available.";
                    return;
                }
            }

            StatusText.Text = "Connecting to the Steward Friends Build...";
            var authentication = await sessionClient.AuthenticateFriendsBuildAsync(
                credential,
                _deviceSettings.InstallationId,
                cancellationToken);
            if (authentication.Status == RemoteFriendsBuildAuthenticationStatus.InvalidCredential)
            {
                if (credentialWasStored)
                {
                    credentialStore.Delete();
                }

                credential = null;
                promptMessage = "That private code is no longer valid. Enter the current Friends Build code.";
                continue;
            }

            if (authentication.Tokens is null || authentication.Identity is null)
            {
                throw new InvalidDataException(
                    "Steward authenticated the Friends Build session without returning complete identity/session data.");
            }

            if (!credentialWasStored)
            {
                await credentialStore.SaveAsync(credential, cancellationToken);
            }

            var user = new UserIdentity(
                authentication.Identity.Provider,
                authentication.Identity.ExternalId,
                authentication.Identity.DisplayName);
            await SetAuthenticatedRemoteRuntimeAsync(
                configuration.ApiBaseAddress,
                authentication.Tokens,
                user,
                cancellationToken);
            StatusText.Text = _lastRemoteWorldLoadError is null
                ? $"Shared Worlds connected as {user.DisplayName}."
                : "Steward authenticated successfully, but shared Worlds are temporarily unavailable. Local Worlds remain available.";
            return;
        }

        StatusText.Text = "The Friends Build code was rejected by Steward. Local Worlds remain available.";
    }

    private string? PromptForFriendsBuildCredential(string? message)
    {
        var dialog = new FriendsBuildCredentialDialog(message)
        {
            Owner = this
        };
        return dialog.ShowDialog() == true ? dialog.Credential : null;
    }
}
