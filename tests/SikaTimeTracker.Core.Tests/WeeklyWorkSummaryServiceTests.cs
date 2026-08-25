using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;

namespace SikaTimeTracker.Core.Tests;

[TestClass]
public sealed class WeeklyWorkSummaryServiceTests
{
    private readonly WeeklyWorkSummaryService _service = new();

    [TestMethod]
    public void Calculate_SumsOnlyCurrentWeekWorkActivities()
    {
        var timeZone = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var activities = new[]
        {
            Segment(1, new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(2), 1),
            Segment(2, new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(3), 1),
            Segment(3, new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(4), 2),
            Segment(4, new DateTimeOffset(2026, 8, 23, 9, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(5), 1)
        };

        var summary = _service.Calculate(
            activities,
            [new Category(1, "工作", "#00AA00", 1), new Category(2, "其他", "#888888", 2, true)],
            now,
            timeZone,
            TimeSpan.FromSeconds(15));

        Assert.IsTrue(summary.HasWorkCategory);
        Assert.AreEqual(new DateOnly(2026, 8, 24), summary.WeekStartDate);
        Assert.AreEqual(TimeSpan.FromHours(5), summary.Duration);
    }

    [TestMethod]
    public void Calculate_ClipsActivitiesAtWeekStartAndCurrentTime()
    {
        var now = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
        var activities = new[]
        {
            Segment(1, now.AddHours(-11), TimeSpan.FromHours(2), 1),
            Segment(2, now.AddHours(-1), TimeSpan.FromHours(3), 1)
        };

        var summary = _service.Calculate(
            activities,
            [new Category(1, "工作", "#00AA00", 1)],
            now,
            TimeZoneInfo.Utc,
            TimeSpan.Zero);

        Assert.AreEqual(TimeSpan.FromHours(2), summary.Duration);
    }

    [TestMethod]
    public void Calculate_ReportsMissingWorkCategory()
    {
        var now = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);

        var summary = _service.Calculate(
            [],
            [new Category(1, "其他", "#888888", 1, true)],
            now,
            TimeZoneInfo.Utc,
            TimeSpan.Zero);

        Assert.IsFalse(summary.HasWorkCategory);
        Assert.AreEqual(TimeSpan.Zero, summary.Duration);
    }

    private static ActivitySegment Segment(
        long id,
        DateTimeOffset start,
        TimeSpan duration,
        long categoryId)
    {
        var end = start + duration;
        return new ActivitySegment(id, start, end, end, "app", "title", categoryId, null, false);
    }
}
