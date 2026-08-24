namespace SikaTimeTracker.Core.Models;

public sealed class SystemActivityChangedEventArgs : EventArgs
{
    public SystemActivityChangedEventArgs(
        bool isInteractive,
        DateTimeOffset observedAtUtc,
        string reason)
    {
        IsInteractive = isInteractive;
        ObservedAtUtc = observedAtUtc;
        Reason = reason;
    }

    public bool IsInteractive { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public string Reason { get; }
}
