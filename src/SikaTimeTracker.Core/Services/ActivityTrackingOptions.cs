namespace SikaTimeTracker.Core.Services;

public sealed record ActivityTrackingOptions
{
    public bool IdleDetectionEnabled { get; init; } = true;

    public TimeSpan IdleThreshold { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan MaximumTrustedGap { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan MinimumActivityDuration { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan AdjacentMergeGap { get; init; } = TimeSpan.FromSeconds(10);
}
