namespace SharedWorlds.Desktop;

/// <summary>
/// Tracks independent shell-busy ownership. Foreground operations and lifecycle-critical work may
/// overlap; releasing one owner must never release the other owner's protection.
/// </summary>
internal sealed class BusyOwnershipState
{
    private bool _foregroundBusy;
    private bool _lifecycleBusy;

    public bool IsBusy => _foregroundBusy || _lifecycleBusy;

    public void SetForeground(bool isBusy)
        => _foregroundBusy = isBusy;

    public void SetLifecycle(bool isBusy)
        => _lifecycleBusy = isBusy;
}
