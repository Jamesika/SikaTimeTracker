using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
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
    private const string ExitEventName = @"Local\SikaTimeTracker.ExitRequest";
    private Window? _window;
    private MainWindow? _mainWindow;
    private IActivityStore? _activityStore;
    private ActivityTrackingService? _trackingService;
    private TrayIconService? _trayIconService;
    private AppInstance? _appInstance;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitRegistration;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var currentInstance = AppInstance.GetCurrent();
        var primaryInstance = AppInstance.FindOrRegisterForKey("SikaTimeTracker.Primary");
        if (!primaryInstance.IsCurrent)
        {
            if (Environment.GetCommandLineArgs().Contains("--exit", StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var exitEvent = EventWaitHandle.OpenExisting(ExitEventName);
                    exitEvent.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                }

                Exit();
                return;
            }

            await primaryInstance.RedirectActivationToAsync(currentInstance.GetActivatedEventArgs());
            Exit();
            return;
        }

        _appInstance = primaryInstance;
        _appInstance.Activated += OnInstanceActivated;
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
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
                MinimumActivityDuration = TimeSpan.FromSeconds(preferences.MinimumActivitySeconds),
                AdjacentMergeGap = TimeSpan.FromSeconds(preferences.MergeGapSeconds)
            });

        var mainWindow = new MainWindow(
            _trackingService,
            _activityStore,
            settingsService,
            startupService,
            preferences,
            dataDirectory);
        _mainWindow = mainWindow;
        _window = mainWindow;
        mainWindow.Activate();
        _trayIconService = new TrayIconService(mainWindow, _trackingService);
        mainWindow.Exiting += (_, _) =>
        {
            _trayIconService?.Dispose();
            _exitRegistration?.Unregister(null);
            _exitEvent?.Dispose();
        };
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(
            _exitEvent,
            (_, _) => mainWindow.DispatcherQueue.TryEnqueue(async () => await mainWindow.ExitAsync()),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: true);
        if (preferences.StartMinimized || HasArgument(args.Arguments, "--minimized"))
        {
            mainWindow.HideToTray();
        }

        await _trackingService.StartAsync();
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        if (_mainWindow is null)
        {
            return;
        }

        var launchArguments = (args.Data as Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs)?.Arguments;
        if (HasArgument(launchArguments, "--exit"))
        {
            _mainWindow.DispatcherQueue.TryEnqueue(async () => await _mainWindow.ExitAsync());
            return;
        }

        _mainWindow.DispatcherQueue.TryEnqueue(_mainWindow.ShowFromTray);
    }

    private static bool HasArgument(string? arguments, string argument)
    {
        return arguments?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(argument, StringComparer.OrdinalIgnoreCase) == true;
    }
}
