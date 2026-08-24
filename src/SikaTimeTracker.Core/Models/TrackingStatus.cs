namespace SikaTimeTracker.Core.Models;

public sealed record TrackingStatus(
    bool IsTracking,
    bool IsPaused,
    bool IsIdle,
    bool IsSystemInteractive,
    string StatusText,
    WindowSnapshot? CurrentWindow = null);
