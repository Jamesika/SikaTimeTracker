namespace SikaTimeTracker.Core.Models;

public sealed record ActivitySegment(
    long Id,
    DateTimeOffset StartTimeUtc,
    DateTimeOffset? EndTimeUtc,
    DateTimeOffset LastHeartbeatUtc,
    string ProcessName,
    string WindowTitle,
    long CategoryId,
    long? ClassificationRuleId,
    bool IsManuallyClassified,
    string WebsiteDomain = "")
{
    public DateTimeOffset EffectiveEndTimeUtc => EndTimeUtc ?? LastHeartbeatUtc;

    public TimeSpan Duration => EffectiveEndTimeUtc > StartTimeUtc
        ? EffectiveEndTimeUtc - StartTimeUtc
        : TimeSpan.Zero;
}
