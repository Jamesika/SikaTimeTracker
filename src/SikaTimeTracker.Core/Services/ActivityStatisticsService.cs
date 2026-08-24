using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Services;

public sealed class ActivityStatisticsService
{
    public IReadOnlyList<DailyActivityTotal> BuildDailyTotals(
        IEnumerable<ActivitySegment> activities,
        DateOnly firstDate,
        DateOnly lastDate,
        TimeZoneInfo timeZone,
        long? categoryId = null)
    {
        if (lastDate < firstDate)
        {
            throw new ArgumentOutOfRangeException(nameof(lastDate));
        }

        var filtered = activities
            .Where(activity => !categoryId.HasValue || activity.CategoryId == categoryId.Value)
            .ToArray();
        var totals = new List<DailyActivityTotal>(lastDate.DayNumber - firstDate.DayNumber + 1);
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            var (dayStartUtc, dayEndUtc) = GetDayBoundsUtc(date, timeZone);
            var ticks = filtered.Sum(activity => GetOverlap(activity, dayStartUtc, dayEndUtc).Ticks);
            totals.Add(new DailyActivityTotal(date, TimeSpan.FromTicks(ticks)));
        }

        return totals;
    }

    public IReadOnlyList<TimelineActivity> BuildTimeline(
        IEnumerable<ActivitySegment> activities,
        DateOnly date,
        TimeZoneInfo timeZone,
        long? categoryId = null)
    {
        var (dayStartUtc, dayEndUtc) = GetDayBoundsUtc(date, timeZone);
        return activities
            .Where(activity => !categoryId.HasValue || activity.CategoryId == categoryId.Value)
            .Select(activity =>
            {
                var startUtc = activity.StartTimeUtc > dayStartUtc ? activity.StartTimeUtc : dayStartUtc;
                var effectiveEndUtc = activity.EffectiveEndTimeUtc;
                var endUtc = effectiveEndUtc < dayEndUtc ? effectiveEndUtc : dayEndUtc;
                return (Activity: activity, StartUtc: startUtc, EndUtc: endUtc);
            })
            .Where(item => item.EndUtc > item.StartUtc)
            .OrderBy(item => item.StartUtc)
            .Select(item => new TimelineActivity(
                item.Activity.Id,
                TimeZoneInfo.ConvertTime(item.StartUtc, timeZone),
                TimeZoneInfo.ConvertTime(item.EndUtc, timeZone),
                item.EndUtc - item.StartUtc,
                item.Activity.ProcessName,
                item.Activity.WindowTitle,
                item.Activity.CategoryId,
                item.Activity.IsManuallyClassified))
            .ToArray();
    }

    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetDayBoundsUtc(
        DateOnly date,
        TimeZoneInfo timeZone)
    {
        var localStart = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var localEnd = DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return (
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone), TimeSpan.Zero),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone), TimeSpan.Zero));
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
