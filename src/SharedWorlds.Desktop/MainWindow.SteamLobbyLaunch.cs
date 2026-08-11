using System.Globalization;
using Steamworks;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private bool _steamLobbyLaunchRequestProcessed;

    /// <summary>
    /// Handles Steam's cold-start lobby invitation form. When Steward is already running Steam raises
    /// GameLobbyJoinRequested_t and MainWindow.WorldJoin handles it directly; when the application is
    /// launched for an accepted invitation Steam supplies +connect_lobby &lt;lobbyId&gt; instead. Both
    /// paths converge on the same private-lobby validation/catch-up handler.
    /// </summary>
    private void InitializeSteamLobbyLaunchRequest()
    {
        if (_steamLobbyLaunchRequestProcessed)
        {
            return;
        }

        _steamLobbyLaunchRequestProcessed = true;
        var parse = SteamLobbyLaunchRequest.Parse(Environment.GetCommandLineArgs());
        if (parse.Problem is not null)
        {
            StatusText.Text = $"Steam lobby invitation could not be opened: {parse.Problem}";
            return;
        }

        if (parse.LobbyId is not { } lobbyId)
        {
            return;
        }

        if (_peerRuntime is null)
        {
            StatusText.Text = _peerRuntimeProblem is null
                ? "Steam launched Steward for a World invitation, but peer Join is unavailable on this launch."
                : $"Steam launched Steward for a World invitation, but peer Join is unavailable: {_peerRuntimeProblem}";
            return;
        }

        // Finish the rest of startup first. The callback path performs the same busy/duplicate checks
        // as an in-process Steam invitation and then uses LobbyJoin -> CatchUp -> local World refresh.
        _ = Dispatcher.BeginInvoke(
            () => PeerWorldLobbyJoinRequested(new CSteamID(lobbyId)),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}

internal readonly record struct SteamLobbyLaunchRequest(
    ulong? LobbyId,
    string? Problem)
{
    private const string ConnectLobbyArgument = "+connect_lobby";

    public static SteamLobbyLaunchRequest Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ulong? parsedLobbyId = null;

        // Environment.GetCommandLineArgs includes the executable at index 0. Searching all entries is
        // harmless and makes this parser deterministic in tests or alternate launch hosts.
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(
                    arguments[index],
                    ConnectLobbyArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parsedLobbyId is not null)
            {
                return new SteamLobbyLaunchRequest(
                    null,
                    "the command line contains more than one +connect_lobby request");
            }

            if (index + 1 >= arguments.Count ||
                !ulong.TryParse(
                    arguments[index + 1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var lobbyId) ||
                lobbyId == 0)
            {
                return new SteamLobbyLaunchRequest(
                    null,
                    "+connect_lobby must be followed by one positive Steam lobby ID");
            }

            parsedLobbyId = lobbyId;
            index++;
        }

        return new SteamLobbyLaunchRequest(parsedLobbyId, null);
    }
}
