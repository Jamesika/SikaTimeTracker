using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;

namespace SikaTimeTracker.Core.Tests;

[TestClass]
public sealed class ActivitySessionMergerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void Build_MergesFourPhotoshopTitlesAndIncludesGapsWithoutChangingOriginals()
    {
        var raw = new[]
        {
            Segment(1, 0, 60), Segment(2, 65, 120),
            Segment(3, 125, 180), Segment(4, 190, 300)
        };
        var session = ActivitySessionMerger.Build(raw.Reverse(), Gap, TimeSpan.Zero).Single();

        Assert.AreEqual(4, session.SegmentCount);
        Assert.AreEqual(TimeSpan.FromMinutes(5), session.Activity.Duration);
        Assert.AreEqual(raw[3].WindowTitle, session.Activity.WindowTitle);
        Assert.AreEqual(raw[0].Id, session.Activity.Id);
        Assert.AreEqual(Start.AddSeconds(60), raw[0].EndTimeUtc);
        Assert.AreEqual(TimeSpan.FromSeconds(280), TimeSpan.FromTicks(raw.Sum(item => item.Duration.Ticks)));
    }

    [TestMethod]
    [DataRow(0, 10, 1)]
    [DataRow(10, 10, 1)]
    [DataRow(11, 10, 2)]
    [DataRow(0, 0, 2)]
    public void Build_RespectsGapLimitAndDisabledSetting(int gapSeconds, int limitSeconds, int count)
    {
        var sessions = ActivitySessionMerger.Build(
            [Segment(1, 0, 60), Segment(2, 60 + gapSeconds, 120)],
            TimeSpan.FromSeconds(limitSeconds), TimeSpan.Zero);
        Assert.HasCount(count, sessions);
    }

    [TestMethod]
    public void Build_MergesBeforeMinimumDurationFilterAndUsesLiveHeartbeat()
    {
        var sessions = ActivitySessionMerger.Build(
            [Segment(1, 0, 8), Segment(2, 10, 18) with { EndTimeUtc = null }],
            Gap, TimeSpan.FromSeconds(15));
        Assert.HasCount(1, sessions);
        Assert.IsNull(sessions[0].Activity.EndTimeUtc);
        Assert.AreEqual(TimeSpan.FromSeconds(18), sessions[0].Activity.Duration);
    }

    [TestMethod]
    public void Build_KeepsProgramsCategoriesAndWebsitesSeparate()
    {
        var first = Segment(1, 0, 60);
        var next = Segment(2, 65, 120);
        foreach (var different in new[]
                 {
                     next with { ProcessName = "Code" },
                     next with { CategoryId = 3 },
                     next with { WebsiteDomain = "example.com" }
                 })
        {
            Assert.HasCount(2, ActivitySessionMerger.Build([first, different], Gap, TimeSpan.Zero));
        }

        Assert.HasCount(1, ActivitySessionMerger.Build(
            [first with { ProcessName = "msedge", WebsiteDomain = "EXAMPLE.COM" },
             next with { ProcessName = "MSEDGE", WebsiteDomain = "example.com" }], Gap, TimeSpan.Zero));
    }

    [TestMethod]
    public void Statistics_HiddenInterveningActivityStillPreventsGapFilling()
    {
        var raw = new[]
        {
            Segment(1, 0, 60),
            Segment(2, 62, 64) with { ProcessName = "Code", CategoryId = 3 },
            Segment(3, 65, 120)
        };
        var stats = new ActivityStatisticsService();
        var timeline = stats.BuildTimeline(raw, new DateOnly(2026, 9, 14), TimeZoneInfo.Utc,
            2, Gap, TimeSpan.FromSeconds(15));
        var daily = stats.BuildDailyTotals(raw, new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 14),
            TimeZoneInfo.Utc, 2, Gap, TimeSpan.FromSeconds(15));
        var weekly = new WeeklyWorkSummaryService().Calculate(raw,
            [new Category(2, "工作", "#00AA00", 1)], Start.AddMinutes(5), TimeZoneInfo.Utc,
            TimeSpan.FromSeconds(15), Gap);

        Assert.HasCount(2, timeline);
        Assert.AreEqual(TimeSpan.FromSeconds(115), daily[0].Duration);
        Assert.AreEqual(daily[0].Duration, weekly.Duration);
    }

    [TestMethod]
    public void Build_PreservesOverlapsAndDoesNotFillGapOccupiedByEarlierLongActivity()
    {
        var raw = new[]
        {
            Segment(1, 0, 180) with { ProcessName = "Other" },
            Segment(2, 20, 60), Segment(3, 65, 120)
        };
        Assert.HasCount(3, ActivitySessionMerger.Build(raw, Gap, TimeSpan.Zero));
        Assert.HasCount(2, ActivitySessionMerger.Build(
            [Segment(1, 0, 120), Segment(2, 60, 180)], Gap, TimeSpan.Zero));
    }

    [TestMethod]
    public void Statistics_MergedGapIsClippedAtMidnightAndWeekStartWithConsistentTotals()
    {
        var raw = new[] { Segment(1, -60, -5), Segment(2, 5, 60), Segment(3, 65, 120) };
        var stats = new ActivityStatisticsService();
        var totals = stats.BuildDailyTotals(raw, new DateOnly(2026, 9, 13), new DateOnly(2026, 9, 14),
            TimeZoneInfo.Utc, 2, Gap);
        var timeline = stats.BuildTimeline(raw, new DateOnly(2026, 9, 14), TimeZoneInfo.Utc, 2, Gap);
        var weekly = new WeeklyWorkSummaryService().Calculate(raw,
            [new Category(2, "工作", "#00AA00", 1)], Start.AddMinutes(3), TimeZoneInfo.Utc, TimeSpan.Zero, Gap);

        Assert.AreEqual(TimeSpan.FromMinutes(1), totals[0].Duration);
        Assert.AreEqual(TimeSpan.FromMinutes(2), totals[1].Duration);
        Assert.AreEqual(Start, timeline.Single().StartLocal);
        Assert.AreEqual(totals[1].Duration, timeline[0].Duration);
        Assert.AreEqual(totals[1].Duration, weekly.Duration);
        Assert.AreEqual(3, timeline[0].SourceSegmentCount);
    }

    [TestMethod]
    public void Statistics_MergedSoftwareTotalsStillDeduplicateParallelIntervals()
    {
        var raw = new[]
        {
            Segment(1, 0, 60), Segment(2, 65, 120),
            Segment(3, 90, 180) with { ProcessName = "Other" }
        };
        var stats = new ActivityStatisticsService();
        var timeline = stats.BuildTimeline(raw, new DateOnly(2026, 9, 14), TimeZoneInfo.Utc, 2, Gap);
        Assert.HasCount(2, timeline);
        Assert.AreEqual(TimeSpan.FromSeconds(120), timeline[0].Duration);
        Assert.AreEqual(TimeSpan.FromSeconds(180),
            ActivityDurationCalculator.Calculate(timeline.Select(item => (item.StartLocal, item.EndLocal))));
        Assert.HasCount(2, stats.AssignTimelineLanes(timeline).Values.Distinct().ToArray());
    }

    private static ActivitySegment Segment(long id, int start, int end) =>
        new(id, Start.AddSeconds(start), Start.AddSeconds(end), Start.AddSeconds(end),
            "Photoshop", $"TA_医保卡.psd（图层 {id}）", 2, null, false);
}
