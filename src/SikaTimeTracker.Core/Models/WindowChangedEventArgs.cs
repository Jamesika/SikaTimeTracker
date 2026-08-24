namespace SikaTimeTracker.Core.Models;

public sealed class WindowChangedEventArgs : EventArgs
{
    public WindowChangedEventArgs(WindowSnapshot? snapshot)
    {
        Snapshot = snapshot;
    }

    public WindowSnapshot? Snapshot { get; }
}
