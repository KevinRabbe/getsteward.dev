using System.Globalization;
using System.Windows.Threading;
using SharedWorlds.Core.Domain;
using Steamworks;

namespace SharedWorlds.Desktop;

/// <summary>
/// Owns the Steam client API for the Steward desktop process lifetime. Authentication tickets,
/// lobbies, invites, and later Steam networking all share this one initialization and callback pump.
/// </summary>
internal sealed class SteamPlatformRuntime : IDisposable
{
    private static readonly TimeSpan CallbackPumpInterval = TimeSpan.FromMilliseconds(25);

    private readonly DispatcherTimer _callbackTimer;
    private bool _disposed;

    private SteamPlatformRuntime(
        uint appId,
        CSteamID localSteamId,
        UserIdentity localUser,
        Dispatcher dispatcher)
    {
        AppId = appId;
        LocalSteamId = localSteamId;
        LocalUser = localUser;
        Dispatcher = dispatcher;
        _callbackTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = CallbackPumpInterval
        };
        _callbackTimer.Tick += OnCallbackTick;
        _callbackTimer.Start();
    }

    public uint AppId { get; }
    public CSteamID LocalSteamId { get; }
    public UserIdentity LocalUser { get; }
    public Dispatcher Dispatcher { get; }

    public static bool TryCreate(
        Dispatcher dispatcher,
        uint expectedAppId,
        out SteamPlatformRuntime? runtime,
        out string? problem)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (expectedAppId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedAppId));
        }

        if (!dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "Steam platform initialization must run on Steward's UI dispatcher.");
        }

        bool initialized;
        try
        {
            initialized = SteamAPI.Init();
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            TypeInitializationException)
        {
            runtime = null;
            problem = $"Steamworks could not be loaded: {exception.Message}";
            return false;
        }

        if (!initialized)
        {
            runtime = null;
            problem =
                "Steam could not initialize. Start Steward through the configured Steam app with the Steam client running.";
            return false;
        }

        try
        {
            var actualAppId = SteamUtils.GetAppID().m_AppId;
            if (actualAppId != expectedAppId)
            {
                SteamAPI.Shutdown();
                runtime = null;
                problem =
                    $"Steam initialized AppID {actualAppId}, but Steward is configured for AppID {expectedAppId}.";
                return false;
            }

            var localSteamId = SteamUser.GetSteamID();
            if (localSteamId.m_SteamID == 0)
            {
                SteamAPI.Shutdown();
                runtime = null;
                problem = "Steam returned an invalid local SteamID64.";
                return false;
            }

            var externalId = localSteamId.m_SteamID.ToString(CultureInfo.InvariantCulture);
            var displayName = SteamFriends.GetPersonaName();
            var localUser = new UserIdentity(
                "steam",
                externalId,
                string.IsNullOrWhiteSpace(displayName) ? externalId : displayName);

            runtime = new SteamPlatformRuntime(
                actualAppId,
                localSteamId,
                localUser,
                dispatcher);
            problem = null;
            return true;
        }
        catch
        {
            SteamAPI.Shutdown();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _callbackTimer.Stop();
        _callbackTimer.Tick -= OnCallbackTick;
        SteamAPI.Shutdown();
    }

    private void OnCallbackTick(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            SteamAPI.RunCallbacks();
        }
    }
}
