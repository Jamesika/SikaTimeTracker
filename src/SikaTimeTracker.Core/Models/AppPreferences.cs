namespace SikaTimeTracker.Core.Models;

public sealed record AppPreferences
{
    public bool RunAtStartup { get; init; }

    public bool StartMinimized { get; init; }

    public bool IdleDetectionEnabled { get; init; } = true;

    public int IdleThresholdMinutes { get; init; } = 5;

    public int MinimumActivitySeconds { get; init; } = 30;

    public int MergeGapSeconds { get; init; } = 10;

    public bool RecordWindowTitles { get; init; } = true;

    public AppTheme Theme { get; init; } = AppTheme.System;
}
