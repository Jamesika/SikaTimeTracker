using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;

namespace SikaTimeTracker.Core.Tests;

[TestClass]
public sealed class ActivityDurationCalculatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Calculate_UnionsUnsortedNestedDuplicateAndAdjacentIntervals()
    {
        var duration = ActivityDurationCalculator.Calculate(
        [
            (Start.AddMinutes(5), Start.AddMinutes(15)),
            (Start.AddMinutes(2), Start.AddMinutes(4)),
            (Start, Start.AddMinutes(10)),
            (Start, Start.AddMinutes(10)),
            (Start.AddMinutes(15), Start.AddMinutes(20)),
            (Start.AddMinutes(30), Start.AddMinutes(35)),
            (Start.AddMinutes(40), Start.AddMinutes(40)),
            (Start.AddMinutes(45), Start.AddMinutes(40))
        ]);

        Assert.AreEqual(TimeSpan.FromMinutes(25), duration);
        Assert.AreEqual(TimeSpan.Zero, ActivityDurationCalculator.Calculate([]));
    }

    [TestMethod]
    public void Calculate_UsesInstantsAcrossDifferentUtcOffsets()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(10), ActivityDurationCalculator.Calculate(
        [
            (Start, Start.AddMinutes(10)),
            (Start.ToOffset(TimeSpan.FromHours(8)), Start.AddMinutes(10).ToOffset(TimeSpan.FromHours(8)))
        ]));
    }

    [TestMethod]
    public void DailyAndWeeklyTotals_DeduplicateParallelWorkAndClipWeekBoundary()
    {
        var raw = new[]
        {
            Segment(1, -5, 10, 2),
            Segment(2, 5, 15, 2),
            Segment(3, 8, 12, 2),
            Segment(4, 10, 20, 1)
        };
        var service = new ActivityStatisticsService();
        var work = service.BuildDailyTotals(raw, new DateOnly(2026, 9, 13),
            new DateOnly(2026, 9, 14), TimeZoneInfo.Utc, 2);
        var all = service.BuildDailyTotals(raw, new DateOnly(2026, 9, 14),
            new DateOnly(2026, 9, 14), TimeZoneInfo.Utc);
        var weekly = new WeeklyWorkSummaryService().Calculate(raw,
            [new Category(2, "工作", "#00AA00", 1)], Start.AddMinutes(12), TimeZoneInfo.Utc, TimeSpan.Zero);

        Assert.AreEqual(TimeSpan.FromMinutes(5), work[0].Duration);
        Assert.AreEqual(TimeSpan.FromMinutes(15), work[1].Duration);
        Assert.AreEqual(TimeSpan.FromMinutes(20), all[0].Duration);
        Assert.AreEqual(TimeSpan.FromMinutes(12), weekly.Duration);
    }

    [TestMethod]
    public void Timeline_ThreeParallelActivitiesUseSeparateLanesAndReuseFreedLane()
    {
        var raw = new[]
        {
            Segment(1, 0, 10, 2),
            Segment(2, 2, 8, 2),
            Segment(3, 3, 6, 2),
            Segment(4, 10, 15, 2)
        };
        var service = new ActivityStatisticsService();
        var timeline = service.BuildTimeline(raw, new DateOnly(2026, 9, 14), TimeZoneInfo.Utc);
        var lanes = service.AssignTimelineLanes(timeline);

        Assert.HasCount(4, timeline);
        Assert.HasCount(3, lanes.Values.Distinct().ToArray());
        Assert.AreEqual(lanes[1], lanes[4]);
        foreach (var lane in timeline.GroupBy(item => lanes[item.ActivityId]))
        {
            var items = lane.OrderBy(item => item.StartLocal).ToArray();
            for (var index = 1; index < items.Length; index++)
            {
                Assert.IsTrue(items[index].StartLocal >= items[index - 1].EndLocal);
            }
        }
        Assert.AreEqual(TimeSpan.FromMinutes(15),
            ActivityDurationCalculator.Calculate(timeline.Select(item => (item.StartLocal, item.EndLocal))));
    }

    [TestMethod]
    public void Statistics_PreservesShortSwitchWithoutExtendingOriginalSoftware()
    {
        var raw = new[]
        {
            Segment(1, 0, 5, 2) with { ProcessName = "Unity" },
            Segment(2, 5, 6, 1),
            Segment(3, 6, 10, 2) with { ProcessName = "Unity" },
            Segment(4, 7, 12, 2)
        };
        var service = new ActivityStatisticsService();
        var totals = service.BuildDailyTotals(raw, new DateOnly(2026, 9, 14),
            new DateOnly(2026, 9, 14), TimeZoneInfo.Utc, 2);
        var timeline = service.BuildTimeline(raw, new DateOnly(2026, 9, 14), TimeZoneInfo.Utc, 2);
        var lanes = service.AssignTimelineLanes(timeline);

        Assert.AreEqual(TimeSpan.FromMinutes(11), totals[0].Duration);
        Assert.AreEqual(Start.AddMinutes(5), timeline.Single(item => item.ActivityId == 1).EndLocal);
        var weekly = new WeeklyWorkSummaryService().Calculate(raw,
            [new Category(2, "工作", "#00AA00", 1)], Start.AddMinutes(12), TimeZoneInfo.Utc, TimeSpan.Zero);
        Assert.AreEqual(totals[0].Duration, weekly.Duration);
        Assert.AreNotEqual(lanes[3], lanes[4]);
    }

    private static ActivitySegment Segment(long id, int start, int end, long category)
    {
        return new ActivitySegment(id, Start.AddMinutes(start), Start.AddMinutes(end), Start.AddMinutes(end),
            $"app{id}", "Window", category, null, false);
    }
}
