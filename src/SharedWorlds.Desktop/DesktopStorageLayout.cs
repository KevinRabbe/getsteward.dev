namespace SharedWorlds.Desktop;

/// <summary>
/// Composes every SafeWorld-owned durable subroot from the one root resolved by
/// <see cref="DesktopLocalDataRoot"/>. This type never discovers LocalApplicationData itself.
/// Components receive the specific subroot they own instead of reconstructing product paths.
/// </summary>
internal sealed class DesktopStorageLayout
{
    private DesktopStorageLayout(string root)
    {
        Root = Path.GetFullPath(root);
        WorldDataRoot = Path.Combine(Root, "data");
        SettingsRoot = Path.Combine(Root, "settings");
        ManagedWorkspacesRoot = Path.Combine(Root, "workspaces");
        RemoteRoot = Path.Combine(Root, "remote");
        LogsRoot = Path.Combine(Root, "logs");
    }

    public string Root { get; }
    public string WorldDataRoot { get; }
    public string SettingsRoot { get; }
    public string ManagedWorkspacesRoot { get; }
    public string RemoteRoot { get; }
    public string LogsRoot { get; }

    public string DeviceSettingsPath => Path.Combine(SettingsRoot, "device.json");
    public string FriendsBuildCredentialPath => Path.Combine(
        SettingsRoot,
        "friends-build-credential.bin");

    public static DesktopStorageLayout FromResolvedRoot()
        => new(DesktopLocalDataRoot.RequireResolvedRoot());

    internal static DesktopStorageLayout FromRootForTests(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return new DesktopStorageLayout(root);
    }
}
