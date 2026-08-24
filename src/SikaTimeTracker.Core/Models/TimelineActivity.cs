namespace SikaTimeTracker.Core.Models;

public sealed record TimelineActivity(
    long ActivityId,
    DateTimeOffset StartLocal,
    DateTimeOffset EndLocal,
    TimeSpan Duration,
    string ProcessName,
    string WindowTitle,
    long CategoryId,
    bool IsManuallyClassified);
