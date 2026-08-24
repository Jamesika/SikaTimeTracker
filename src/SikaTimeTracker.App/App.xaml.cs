using Microsoft.UI.Xaml;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;
using SikaTimeTracker.Services;
using SikaTimeTracker.Infrastructure.Data;
using SikaTimeTracker.Infrastructure.SystemIntegration;
using SikaTimeTracker.Infrastructure.Tracking;

namespace SikaTimeTracker;

public partial class App : Application
{
    private Window? _window;
    private IActivityStore? _activityStore;
    private ActivityTrackingService? _trackingService;
    private TrayIconService? _trayIconService;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SikaTimeTracker");
        _activityStore = new SqliteActivityStore(Path.Combine(dataDirectory, "activity.db"));
        await _activityStore.InitializeAsync();
        var settingsService = new ApplicationSettingsService(_activityStore);
        var preferences = await settingsService.LoadAsync();
        var startupService = new WindowsStartupService(
            Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SikaTimeTracker.exe"));
        var windowSource = new WindowsForegroundWindowSource
        {
            CaptureWindowTitles = preferences.RecordWindowTitles
        };
        _trackingService = new ActivityTrackingService(
            _activityStore,
            windowSource,
            new WindowsSystemActivitySource(),
            new ClassificationEngine(),
            new ActivityTrackingOptions
            {
                IdleDetectionEnabled = preferences.IdleDetectionEnabled,
                IdleThreshold = TimeSpan.FromMinutes(preferences.IdleThresholdMinutes),
                MinimumActivityDuration = TimeSpan.FromSeconds(preferences.MinimumActivitySeconds)
            });

        var mainWindow = new MainWindow(
            _trackingService,
            _activityStore,
            settingsService,
            startupService,
            preferences,
            dataDirectory);
        _window = mainWindow;
        mainWindow.Activate();
        _trayIconService = new TrayIconService(mainWindow, _trackingService);
        if (preferences.StartMinimized
            || args.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("--minimized", StringComparer.OrdinalIgnoreCase))
        {
            mainWindow.HideToTray();
        }

        await _trackingService.StartAsync();
    }
}
