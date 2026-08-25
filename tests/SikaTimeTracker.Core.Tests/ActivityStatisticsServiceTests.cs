using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;

namespace SikaTimeTracker.Core.Tests;

[TestClass]
public sealed class ActivityStatisticsServiceTests
{
    private readonly ActivityStatisticsService _service = new();

    [TestMethod]
    public void BuildDailyTotals_SplitsActivityAcrossMidnight()
    {
        var activity = Segment(
            new DateTimeOffset(2026, 8, 24, 23, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero));

        var totals = _service.BuildDailyTotals(
            [activity],
            new DateOnly(2026, 8, 24),
            new DateOnly(2026, 8, 25),
            TimeZoneInfo.Utc);

        Assert.AreEqual(TimeSpan.FromMinutes(30), totals[0].Duration);
        Assert.AreEqual(TimeSpan.FromHours(1), totals[1].Duration);
    }

    [TestMethod]
    public void BuildDailyTotals_AppliesCategoryFilter()
    {
        var first = Segment(
            new DateTimeOffset(2026, 8, 25, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero),
            categoryId: 2);
        var second = Segment(
            new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 25, 11, 0, 0, TimeSpan.Zero),
            categoryId: 3);

        var total = _service.BuildDailyTotals(
            [first, second],
            new DateOnly(2026, 8, 25),
            new DateOnly(2026, 8, 25),
            TimeZoneInfo.Utc,
            categoryId: 2).Single();

        Assert.AreEqual(TimeSpan.FromHours(1), total.Duration);
    }

    [TestMethod]
    public void BuildTimeline_ClipsToSelectedDay()
    {
        var activity = Segment(
            new DateTimeOffset(2026, 8, 24, 23, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero));

        var timeline = _service.BuildTimeline(
            [activity],
            new DateOnly(2026, 8, 25),
            TimeZoneInfo.Utc);

        Assert.HasCount(1, timeline);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 25, 0, 0, 0, TimeSpan.Zero), timeline[0].StartLocal);
        Assert.AreEqual(TimeSpan.FromHours(1), timeline[0].Duration);
    }

    [TestMethod]
    public void AssignTimelineLanes_ReusesRowsAndSeparatesOverlaps()
    {
        var start = new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
        var activities = new[]
        {
            Timeline(1, start, start.AddHours(2)),
            Timeline(2, start.AddMinutes(30), start.AddHours(1)),
            Timeline(3, start.AddHours(2), start.AddHours(3))
        };

        var lanes = _service.AssignTimelineLanes(activities);

        Assert.AreEqual(0, lanes[1]);
        Assert.AreEqual(1, lanes[2]);
        Assert.AreEqual(0, lanes[3]);
    }

    private static ActivitySegment Segment(
        DateTimeOffset start,
        DateTimeOffset end,
        long categoryId = 2)
    {
        return new ActivitySegment(
            1,
            start,
            end,
            end,
            "Code",
            "Editor",
            categoryId,
            null,
            false);
    }

    private static TimelineActivity Timeline(
        long id,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        return new TimelineActivity(
            id,
            start,
            end,
            end - start,
            "Code",
            "Editor",
            2,
            false);
    }
}
