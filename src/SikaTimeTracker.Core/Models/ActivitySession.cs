namespace SikaTimeTracker.Core.Models;

// A read-only projection; the original records remain in the store.
public sealed record ActivitySession(ActivitySegment Activity, int SegmentCount);
