namespace SikaTimeTracker.Core.Models;

public sealed record AppPreferences
{
    public const int DefaultIdleThresholdMinutes = 5;
    public const int DefaultMinimumActivitySeconds = 5;
    public const int DefaultMergeGapSeconds = 60;

    public bool RunAtStartup { get; init; }

    public bool StartMinimized { get; init; }

    public bool IdleDetectionEnabled { get; init; } = true;

    public int IdleThresholdMinutes { get; init; } = DefaultIdleThresholdMinutes;

    public int MinimumActivitySeconds { get; init; } = DefaultMinimumActivitySeconds;

    public int MergeGapSeconds { get; init; } = DefaultMergeGapSeconds;

    public bool RecordWindowTitles { get; init; } = true;

    public AppTheme Theme { get; init; } = AppTheme.System;
}
