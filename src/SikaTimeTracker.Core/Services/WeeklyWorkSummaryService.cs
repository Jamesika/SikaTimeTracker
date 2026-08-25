using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Services;

public sealed class WeeklyWorkSummaryService
{
    public WeeklyWorkSummary Calculate(
        IEnumerable<ActivitySegment> activities,
        IEnumerable<Category> categories,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        TimeSpan minimumActivityDuration)
    {
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (minimumActivityDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumActivityDuration));
        }

        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var weekStartDate = today.AddDays(-daysSinceMonday);
        var (weekStartUtc, _) = ActivityStatisticsService.GetDayBoundsUtc(weekStartDate, timeZone);
        var workCategory = categories.FirstOrDefault(category =>
            string.Equals(category.Name, "工作", StringComparison.OrdinalIgnoreCase));
        if (workCategory is null)
        {
            return new WeeklyWorkSummary(false, TimeSpan.Zero, weekStartDate);
        }

        var ticks = activities
            .Where(activity => activity.CategoryId == workCategory.Id
                               && activity.Duration >= minimumActivityDuration)
            .Sum(activity => GetOverlap(activity, weekStartUtc, nowUtc).Ticks);
        return new WeeklyWorkSummary(true, TimeSpan.FromTicks(ticks), weekStartDate);
    }

    private static TimeSpan GetOverlap(
        ActivitySegment activity,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc)
    {
        var startUtc = activity.StartTimeUtc > rangeStartUtc ? activity.StartTimeUtc : rangeStartUtc;
        var effectiveEndUtc = activity.EffectiveEndTimeUtc;
        var endUtc = effectiveEndUtc < rangeEndUtc ? effectiveEndUtc : rangeEndUtc;
        return endUtc > startUtc ? endUtc - startUtc : TimeSpan.Zero;
    }
}
