namespace SikaTimeTracker.Core.Models;

public sealed record Category(
    long Id,
    string Name,
    string Color,
    int SortOrder,
    bool IsDefault = false);
