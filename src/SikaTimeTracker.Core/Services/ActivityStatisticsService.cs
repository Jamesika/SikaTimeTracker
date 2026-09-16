using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Services;

public sealed class ActivityStatisticsService
{
    public IReadOnlyList<DailyActivityTotal> BuildDailyTotals(
        IEnumerable<ActivitySegment> activities,
        DateOnly firstDate,
        DateOnly lastDate,
        TimeZoneInfo timeZone,
        long? categoryId = null,
        TimeSpan? maximumMergeGap = null,
        TimeSpan minimumActivityDuration = default)
    {
        if (lastDate < firstDate)
        {
            throw new ArgumentOutOfRangeException(nameof(lastDate));
        }

        var filtered = ActivitySessionMerger.Build(
                activities, maximumMergeGap ?? TimeSpan.FromSeconds(AppPreferences.DefaultMergeGapSeconds), minimumActivityDuration)
            .Select(session => session.Activity)
            .Where(activity => !categoryId.HasValue || activity.CategoryId == categoryId.Value)
            .ToArray();
        var dayCount = lastDate.DayNumber - firstDate.DayNumber + 1;
        var offset = firstDate.DayNumber;
        var intervalsByDay = Enumerable.Range(0, dayCount)
            .Select(_ => new List<(DateTimeOffset Start, DateTimeOffset End)>()).ToArray();
        foreach (var activity in filtered)
        {
            var effectiveEndUtc = activity.EffectiveEndTimeUtc;
            if (effectiveEndUtc <= activity.StartTimeUtc)
            {
                continue;
            }

            var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(activity.StartTimeUtc, timeZone).DateTime);
            var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(effectiveEndUtc, timeZone).DateTime);
            for (var date = firstDay; date <= lastDay; date = date.AddDays(1))
            {
                if (date < firstDate || date > lastDate)
                {
                    continue;
                }

                var (dayStartUtc, dayEndUtc) = GetDayBoundsUtc(date, timeZone);
                intervalsByDay[date.DayNumber - offset].Add((
                    activity.StartTimeUtc > dayStartUtc ? activity.StartTimeUtc : dayStartUtc,
                    effectiveEndUtc < dayEndUtc ? effectiveEndUtc : dayEndUtc));
            }
        }

        var totals = new List<DailyActivityTotal>(dayCount);
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            totals.Add(new DailyActivityTotal(date,
                ActivityDurationCalculator.Calculate(intervalsByDay[date.DayNumber - offset])));
        }

        return totals;
    }

    public IReadOnlyList<TimelineActivity> BuildTimeline(
        IEnumerable<ActivitySegment> activities,
        DateOnly date,
        TimeZoneInfo timeZone,
        long? categoryId = null,
        TimeSpan? maximumMergeGap = null,
        TimeSpan minimumActivityDuration = default)
    {
        var (dayStartUtc, dayEndUtc) = GetDayBoundsUtc(date, timeZone);
        return ActivitySessionMerger.Build(
                activities, maximumMergeGap ?? TimeSpan.FromSeconds(AppPreferences.DefaultMergeGapSeconds), minimumActivityDuration)
            .Where(session => !categoryId.HasValue || session.Activity.CategoryId == categoryId.Value)
            .Select(session =>
            {
                var activity = session.Activity;
                var startUtc = activity.StartTimeUtc > dayStartUtc ? activity.StartTimeUtc : dayStartUtc;
                var effectiveEndUtc = activity.EffectiveEndTimeUtc;
                var endUtc = effectiveEndUtc < dayEndUtc ? effectiveEndUtc : dayEndUtc;
                return (Activity: activity, StartUtc: startUtc, EndUtc: endUtc, session.SegmentCount);
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
                item.Activity.IsManuallyClassified,
                item.Activity.WebsiteDomain)
            {
                SourceSegmentCount = item.SegmentCount
            })
            .ToArray();
    }

    public IReadOnlyDictionary<long, int> AssignTimelineLanes(
        IEnumerable<TimelineActivity> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);
        var laneEndTimes = new List<DateTimeOffset>();
        var assignments = new Dictionary<long, int>();
        foreach (var activity in activities
                     .OrderBy(item => item.StartLocal)
                     .ThenBy(item => item.EndLocal))
        {
            var lane = -1;
            for (var index = 0; index < laneEndTimes.Count; index++)
            {
                if (laneEndTimes[index] <= activity.StartLocal)
                {
                    lane = index;
                    break;
                }
            }

            if (lane < 0)
            {
                lane = laneEndTimes.Count;
                laneEndTimes.Add(activity.EndLocal);
            }
            else
            {
                laneEndTimes[lane] = activity.EndLocal;
            }

            assignments[activity.ActivityId] = lane;
        }

        return assignments;
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

}
