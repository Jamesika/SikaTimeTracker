namespace SikaTimeTracker.Core.Models;

public sealed record ActivityDraft(
    DateTimeOffset StartTimeUtc,
    string ProcessName,
    string WindowTitle,
    long CategoryId,
    long? ClassificationRuleId = null,
    bool IsManuallyClassified = false,
    string WebsiteDomain = "");
