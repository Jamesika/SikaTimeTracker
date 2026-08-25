using Microsoft.Data.Sqlite;
using System.Runtime.Versioning;
using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Infrastructure.Data;
using SikaTimeTracker.Core.Services;
using SikaTimeTracker.Infrastructure.SystemIntegration;

namespace SikaTimeTracker.Infrastructure.Tests;

[TestClass]
public sealed class SqliteActivityStoreTests
{
    private string _testDirectory = null!;
    private string _databasePath = null!;
    private SqliteActivityStore _store = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "SikaTimeTracker.Tests", Guid.NewGuid().ToString("N"));
        _databasePath = Path.Combine(_testDirectory, "activity.db");
        _store = new SqliteActivityStore(_databasePath);
        await _store.InitializeAsync();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Initialize_SeedsExpectedCategories()
    {
        var categories = await _store.GetCategoriesAsync();

        CollectionAssert.AreEqual(
            new[] { "工作", "游戏", "其他" },
            categories.Select(category => category.Name).ToArray());
        Assert.AreEqual(1, categories.Count(category => category.IsDefault));
    }

    [TestMethod]
    public async Task ActivityLifecycle_PersistsHeartbeatAndEndTime()
    {
        var start = new DateTimeOffset(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
        var activityId = await _store.StartActivityAsync(new ActivityDraft(
            start,
            "Code",
            "SikaTimeTracker - Visual Studio Code",
            CategoryId: 2));

        Assert.IsTrue(await _store.UpdateHeartbeatAsync(activityId, start.AddMinutes(5)));
        Assert.IsTrue(await _store.StopActivityAsync(activityId, start.AddMinutes(10)));

        var activities = await _store.GetActivitiesAsync(start.AddHours(-1), start.AddHours(1));
        Assert.HasCount(1, activities);
        Assert.AreEqual(TimeSpan.FromMinutes(10), activities[0].Duration);
        Assert.AreEqual(start.AddMinutes(10), activities[0].EndTimeUtc);
    }

    [TestMethod]
    public async Task RecoverOpenActivities_StopsAtLastTrustedHeartbeat()
    {
        var start = new DateTimeOffset(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
        var heartbeat = start.AddMinutes(2);
        var activityId = await _store.StartActivityAsync(new ActivityDraft(start, "Code", "Editor", 2));
        await _store.UpdateHeartbeatAsync(activityId, heartbeat);

        Assert.AreEqual(1, await _store.RecoverOpenActivitiesAsync());

        var activities = await _store.GetActivitiesAsync(start.AddMinutes(-1), start.AddMinutes(10));
        Assert.HasCount(1, activities);
        Assert.AreEqual(heartbeat, activities[0].EndTimeUtc);
        Assert.IsFalse(await _store.StopActivityAsync(activityId, start.AddMinutes(8)));
    }

    [TestMethod]
    public async Task CategoriesRulesAndSettings_RoundTrip()
    {
        var category = await _store.SaveCategoryAsync(new Category(0, "学习", "#00AA88", 30));
        var rule = await _store.SaveRuleAsync(new ClassificationRule(
            0,
            category.Id,
            RuleTarget.WindowTitle,
            RuleMatchType.RegularExpression,
            "docs|learn",
            IgnoreCase: true,
            Priority: 25));
        await _store.SetSettingAsync("IdleMinutes", "5");

        Assert.IsGreaterThan(0, category.Id);
        Assert.IsGreaterThan(0, rule.Id);
        Assert.AreEqual("5", await _store.GetSettingAsync("IdleMinutes"));
        Assert.IsTrue((await _store.GetRulesAsync()).Any(savedRule => savedRule.Id == rule.Id));
    }

    [TestMethod]
    public async Task HistoricalReclassification_PreservesManualOverrides()
    {
        var rule = await _store.SaveRuleAsync(new ClassificationRule(
            0,
            CategoryId: 2,
            RuleTarget.WindowTitle,
            RuleMatchType.Contains,
            "Code",
            IgnoreCase: true,
            Priority: 10));
        Assert.IsGreaterThan(0, rule.Id);

        var start = new DateTimeOffset(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
        var automaticId = await _store.StartActivityAsync(new ActivityDraft(start, "Code", "Code Editor", 1));
        await _store.StopActivityAsync(automaticId, start.AddMinutes(1));
        var manualId = await _store.StartActivityAsync(new ActivityDraft(
            start.AddMinutes(2),
            "Code",
            "Code Editor",
            CategoryId: 3,
            IsManuallyClassified: true));
        await _store.StopActivityAsync(manualId, start.AddMinutes(3));

        var service = new HistoricalReclassificationService(_store, new ClassificationEngine());
        Assert.AreEqual(1, await service.ReclassifyAsync());

        var activities = await _store.GetAllActivitiesAsync();
        Assert.AreEqual(2, activities.Single(activity => activity.Id == automaticId).CategoryId);
        Assert.AreEqual(3, activities.Single(activity => activity.Id == manualId).CategoryId);
    }

    [TestMethod]
    public async Task PreferencesAndCsvExport_RoundTripWithoutLosingWindowData()
    {
        var settings = new ApplicationSettingsService(_store);
        var preferences = new AppPreferences
        {
            IdleDetectionEnabled = false,
            IdleThresholdMinutes = 12,
            MinimumActivitySeconds = 4,
            RecordWindowTitles = false,
            Theme = AppTheme.Dark
        };
        await settings.SaveAsync(preferences);
        var loaded = await settings.LoadAsync();
        Assert.AreEqual(preferences, loaded);

        var start = new DateTimeOffset(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
        var activityId = await _store.StartActivityAsync(new ActivityDraft(start, "Code", "Project, \"quoted\"", 2));
        await _store.StopActivityAsync(activityId, start.AddMinutes(1));
        var exporter = new ActivityCsvExporter(_store);
        var exportPath = await exporter.ExportAsync(Path.Combine(_testDirectory, "exports"));
        var csv = await File.ReadAllTextAsync(exportPath);
        StringAssert.Contains(csv, "Project, \"\"quoted\"\"");
    }

    [TestMethod]
    public async Task DefaultPreferences_UseThirtySecondMinimumActivity()
    {
        var settings = new ApplicationSettingsService(_store);

        var preferences = await settings.LoadAsync();

        Assert.AreEqual(30, preferences.MinimumActivitySeconds);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void StartupCommand_QuotesPortableExecutablePath()
    {
        var command = WindowsStartupService.BuildCommandLine(@"C:\Portable Apps\SikaTimeTracker.exe", startMinimized: true);

        Assert.AreEqual("\"C:\\Portable Apps\\SikaTimeTracker.exe\" --minimized", command);
    }

    [TestMethod]
    public async Task AdjacentSameWindowActivities_MergeWithinConfiguredGap()
    {
        var start = new DateTimeOffset(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
        var firstId = await _store.StartActivityAsync(new ActivityDraft(start, "Code", "Editor", 2));
        await _store.StopActivityAsync(firstId, start.AddMinutes(1));
        var secondId = await _store.StartActivityAsync(new ActivityDraft(
            start.AddMinutes(1).AddSeconds(5),
            "Code",
            "Editor",
            2));
        await _store.StopActivityAsync(secondId, start.AddMinutes(2));

        Assert.IsTrue(await _store.TryMergeWithPreviousAsync(secondId, TimeSpan.FromSeconds(10)));

        var activities = await _store.GetAllActivitiesAsync();
        Assert.HasCount(1, activities);
        Assert.AreEqual(firstId, activities[0].Id);
        Assert.AreEqual(start.AddMinutes(2), activities[0].EndTimeUtc);
    }
}
