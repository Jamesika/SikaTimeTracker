namespace SikaTimeTracker.Core.Models;

public sealed record WindowSnapshot(
    nint WindowHandle,
    string ProcessName,
    string WindowTitle,
    DateTimeOffset ObservedAtUtc)
{
    public bool RepresentsSameActivity(WindowSnapshot other)
    {
        return WindowHandle == other.WindowHandle
               && string.Equals(ProcessName, other.ProcessName, StringComparison.Ordinal)
               && string.Equals(WindowTitle, other.WindowTitle, StringComparison.Ordinal);
    }
}
