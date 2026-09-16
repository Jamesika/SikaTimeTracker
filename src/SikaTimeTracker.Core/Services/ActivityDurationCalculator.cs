namespace SikaTimeTracker.Core.Services;

public static class ActivityDurationCalculator
{
    public static TimeSpan Calculate(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        DateTimeOffset? start = null;
        var end = DateTimeOffset.MinValue;
        var duration = TimeSpan.Zero;
        foreach (var interval in intervals.Where(item => item.End > item.Start).OrderBy(item => item.Start))
        {
            if (!start.HasValue)
            {
                start = interval.Start;
                end = interval.End;
            }
            else if (interval.Start <= end)
            {
                end = interval.End > end ? interval.End : end;
            }
            else
            {
                duration += end - start.Value;
                start = interval.Start;
                end = interval.End;
            }
        }

        return start.HasValue ? duration + (end - start.Value) : TimeSpan.Zero;
    }
}
