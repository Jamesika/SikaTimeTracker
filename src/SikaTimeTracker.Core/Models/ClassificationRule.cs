namespace SikaTimeTracker.Core.Models;

public sealed record ClassificationRule(
    long Id,
    long CategoryId,
    RuleTarget Target,
    RuleMatchType MatchType,
    string Pattern,
    bool IgnoreCase,
    int Priority,
    bool IsEnabled = true);
