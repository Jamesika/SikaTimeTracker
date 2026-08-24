namespace SikaTimeTracker.Core.Models;

public sealed record ClassificationResult(
    long CategoryId,
    long? RuleId,
    string? RuleError = null);
