namespace SikaTimeTracker.Core.Models;

public sealed record WeeklyWorkSummary(
    bool HasWorkCategory,
    TimeSpan Duration,
    DateOnly WeekStartDate);
