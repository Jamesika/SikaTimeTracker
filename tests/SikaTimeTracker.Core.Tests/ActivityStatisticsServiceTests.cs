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
}
