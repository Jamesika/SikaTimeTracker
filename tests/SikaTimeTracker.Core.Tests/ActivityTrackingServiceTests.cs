using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;

namespace SikaTimeTracker.Core.Tests;

[TestClass]
public sealed class ActivityTrackingServiceTests
{
    [TestMethod]
    public async Task LockAndUnlock_DoNotRecordLockedInterval()
    {
        var start = DateTimeOffset.UtcNow;
        var fixture = await TrackingFixture.CreateAsync(start);
        await using var tracker = fixture.Tracker;

        await tracker.ProcessSystemActivityAsync(new SystemActivityChangedEventArgs(
            false,
            start.AddMinutes(2),
            "SessionLock"));
        fixture.System.IsInteractive = true;
        await tracker.ProcessSystemActivityAsync(new SystemActivityChangedEventArgs(
            true,
            start.AddMinutes(32),
            "SessionUnlock"));

        Assert.HasCount(2, fixture.Store.Activities);
        Assert.AreEqual(start.AddMinutes(2), fixture.Store.Activities[0].EndTimeUtc);
        Assert.AreEqual(start.AddMinutes(32), fixture.Store.Activities[1].StartTimeUtc);
    }

    [TestMethod]
    public async Task IdleThreshold_StopsAtThresholdBoundary()
    {
        var start = DateTimeOffset.UtcNow;
        var fixture = await TrackingFixture.CreateAsync(start);
        await using var tracker = fixture.Tracker;
        for (var minute = 1; minute <= 5; minute++)
        {
            fixture.System.IdleDuration = TimeSpan.FromMinutes(minute);
            await tracker.ProcessHealthCheckAsync(start.AddMinutes(minute));
        }

        Assert.HasCount(1, fixture.Store.Activities);
        Assert.AreEqual(start.AddMinutes(5), fixture.Store.Activities[0].EndTimeUtc);
        Assert.IsTrue(tracker.Status.IsIdle);
    }

    [TestMethod]
    public async Task UnexpectedTimeGap_StopsAtLastHeartbeatInsteadOfBackfilling()
    {
        var start = DateTimeOffset.UtcNow;
        var fixture = await TrackingFixture.CreateAsync(start);
        await using var tracker = fixture.Tracker;

        await tracker.ProcessHealthCheckAsync(start.AddSeconds(15));
        await tracker.ProcessHealthCheckAsync(start.AddMinutes(10));

        Assert.HasCount(2, fixture.Store.Activities);
        Assert.AreEqual(start.AddSeconds(15), fixture.Store.Activities[0].EndTimeUtc);
        Assert.AreEqual(start.AddMinutes(10), fixture.Store.Activities[1].StartTimeUtc);
    }

    [TestMethod]
    public async Task ForegroundChange_ClosesPreviousAndStartsNextWindow()
    {
        var start = DateTimeOffset.UtcNow;
        var fixture = await TrackingFixture.CreateAsync(start);
        await using var tracker = fixture.Tracker;
        var next = new WindowSnapshot(2, "steam", "Library", start.AddMinutes(3));

        await tracker.ProcessForegroundWindowAsync(next);

        Assert.HasCount(2, fixture.Store.Activities);
        Assert.AreEqual(start.AddMinutes(3), fixture.Store.Activities[0].EndTimeUtc);
        Assert.AreEqual("steam", fixture.Store.Activities[1].ProcessName);
    }

    [TestMethod]
    public async Task ForegroundChange_RaisesActivityRecordedAfterSegmentIsFinalized()
    {
        var start = DateTimeOffset.UtcNow;
        var fixture = await TrackingFixture.CreateAsync(start);
        await using var tracker = fixture.Tracker;
        var recordedCount = 0;
        tracker.ActivityRecorded += (_, _) => recordedCount++;

        await tracker.ProcessForegroundWindowAsync(
            new WindowSnapshot(2, "steam", "Library", start.AddMinutes(3)));

        Assert.AreEqual(1, recordedCount);
    }

    [TestMethod]
    [DataRow("explorer.exe")]
    [DataRow("SikaTimeTracker")]
    public async Task ExcludedProcess_IsNotRecorded(string processName)
    {
        var start = DateTimeOffset.UtcNow;
        var fixture = await TrackingFixture.CreateAsync(
            start,
            new WindowSnapshot(1, processName, "Excluded", start));
        await using var tracker = fixture.Tracker;

        Assert.HasCount(0, fixture.Store.Activities);
    }

    private sealed class TrackingFixture
    {
        private TrackingFixture(
            ActivityTrackingService tracker,
            FakeActivityStore store,
            FakeSystemActivitySource system)
        {
            Tracker = tracker;
            Store = store;
            System = system;
        }

        public ActivityTrackingService Tracker { get; }

        public FakeActivityStore Store { get; }

        public FakeSystemActivitySource System { get; }

        public static async Task<TrackingFixture> CreateAsync(
            DateTimeOffset start,
            WindowSnapshot? initialWindow = null)
        {
            var store = new FakeActivityStore();
            var window = new FakeForegroundWindowSource
            {
                Current = initialWindow ?? new WindowSnapshot(1, "Code", "Editor", start)
            };
            var system = new FakeSystemActivitySource();
            var tracker = new ActivityTrackingService(
                store,
                window,
                system,
                new ClassificationEngine(),
                new ActivityTrackingOptions
                {
                    PollInterval = TimeSpan.FromHours(1),
                    MinimumActivityDuration = TimeSpan.Zero
                });
            await tracker.StartAsync();
            return new TrackingFixture(tracker, store, system);
        }
    }

    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public event EventHandler<WindowChangedEventArgs>? ForegroundWindowChanged
        {
            add { }
            remove { }
        }

        public WindowSnapshot? Current { get; set; }

        public bool CaptureWindowTitles { get; set; } = true;

        public WindowSnapshot? GetCurrentWindow() => Current;

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeSystemActivitySource : ISystemActivitySource
    {
        public event EventHandler<SystemActivityChangedEventArgs>? SystemActivityChanged
        {
            add { }
            remove { }
        }

        public bool IsInteractive { get; set; } = true;

        public TimeSpan IdleDuration { get; set; }

        public bool IsSessionInteractive => IsInteractive;

        public TimeSpan GetIdleDuration() => IdleDuration;

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeActivityStore : IActivityStore
    {
        private long _nextId = 1;

        public List<ActivitySegment> Activities { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Category>>([new Category(1, "其他", "#888888", 1, true)]);
        }

        public Task<Category> SaveCategoryAsync(Category category, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> DeleteCategoryAsync(long categoryId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<ClassificationRule>> GetRulesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ClassificationRule>>([]);
        }

        public Task<ClassificationRule> SaveRuleAsync(ClassificationRule rule, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteRuleAsync(long ruleId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<long> StartActivityAsync(ActivityDraft activity, CancellationToken cancellationToken = default)
        {
            var id = _nextId++;
            Activities.Add(new ActivitySegment(
                id,
                activity.StartTimeUtc,
                null,
                activity.StartTimeUtc,
                activity.ProcessName,
                activity.WindowTitle,
                activity.CategoryId,
                activity.ClassificationRuleId,
                activity.IsManuallyClassified));
            return Task.FromResult(id);
        }

        public Task<bool> UpdateHeartbeatAsync(long activityId, DateTimeOffset heartbeatUtc, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Update(activityId, activity => activity with { LastHeartbeatUtc = heartbeatUtc }));
        }

        public Task<bool> StopActivityAsync(long activityId, DateTimeOffset endTimeUtc, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Update(activityId, activity => activity with
            {
                EndTimeUtc = endTimeUtc,
                LastHeartbeatUtc = endTimeUtc
            }));
        }

        public Task<bool> TryMergeWithPreviousAsync(long activityId, TimeSpan maximumGap, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<bool> DeleteActivityAsync(long activityId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Activities.RemoveAll(activity => activity.Id == activityId) > 0);
        }

        public Task<int> RecoverOpenActivitiesAsync(CancellationToken cancellationToken = default)
        {
            var recovered = 0;
            for (var index = 0; index < Activities.Count; index++)
            {
                if (Activities[index].EndTimeUtc is null)
                {
                    Activities[index] = Activities[index] with { EndTimeUtc = Activities[index].LastHeartbeatUtc };
                    recovered++;
                }
            }

            return Task.FromResult(recovered);
        }

        public Task<IReadOnlyList<ActivitySegment>> GetActivitiesAsync(DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtc, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ActivitySegment>>(Activities);
        }

        public Task<IReadOnlyList<ActivitySegment>> GetAllActivitiesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ActivitySegment>>(Activities);
        }

        public Task<int> DeleteAllActivitiesAsync(CancellationToken cancellationToken = default)
        {
            var count = Activities.Count;
            Activities.Clear();
            return Task.FromResult(count);
        }

        public Task<bool> UpdateActivityClassificationAsync(long activityId, long categoryId, long? ruleId, bool isManual, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Update(activityId, activity => activity with
            {
                CategoryId = categoryId,
                ClassificationRuleId = ruleId,
                IsManuallyClassified = isManual
            }));
        }

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        private bool Update(long activityId, Func<ActivitySegment, ActivitySegment> update)
        {
            var index = Activities.FindIndex(activity => activity.Id == activityId);
            if (index < 0)
            {
                return false;
            }

            Activities[index] = update(Activities[index]);
            return true;
        }
    }
}
