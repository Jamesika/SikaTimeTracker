using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Services;

public sealed class WeeklyWorkSummaryService
{
    public WeeklyWorkSummary Calculate(
        IEnumerable<ActivitySegment> activities,
        IEnumerable<Category> categories,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        TimeSpan minimumActivityDuration,
        TimeSpan? maximumMergeGap = null)
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

        var intervals = ActivitySessionMerger.Build(
                activities, maximumMergeGap ?? TimeSpan.FromSeconds(AppPreferences.DefaultMergeGapSeconds), minimumActivityDuration)
            .Select(session => session.Activity)
            .Where(activity => activity.CategoryId == workCategory.Id)
            .Select(activity => (
                activity.StartTimeUtc > weekStartUtc ? activity.StartTimeUtc : weekStartUtc,
                activity.EffectiveEndTimeUtc < nowUtc ? activity.EffectiveEndTimeUtc : nowUtc));
        return new WeeklyWorkSummary(true, ActivityDurationCalculator.Calculate(intervals), weekStartDate);
    }
}
