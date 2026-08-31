using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;
using SikaTimeTracker.Views;
using Windows.Graphics;

namespace SikaTimeTracker;

public sealed partial class MainWindow : Window
{
    private const uint WindowMessageSetIcon = 0x0080;
    private const nuint SmallIcon = 0;
    private const nuint BigIcon = 1;
    private const string WindowIconResourceName = "SikaTimeTracker.Assets.SikaTimeTracker.ico";
    private readonly ActivityTrackingService _trackingService;
    private readonly IActivityStore _activityStore;
    private readonly ApplicationSettingsService _settingsService;
    private readonly IStartupService _startupService;
    private readonly string _dataDirectory;
    private readonly DispatcherTimer _trackingStatusTimer;
    private AppPreferences _preferences;
    private TrackingStatus? _lastTrackingStatus;
    private bool _exitRequested;
    private bool _isChangingSelection;
    private bool _isWindowActive = true;
    private bool _isWindowVisible = true;
    private string _currentTag = "activity";
    private FrameworkElement? _currentPage;
    private readonly Dictionary<string, FrameworkElement> _pageCache = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _pageSwitchTimer;
    private string? _pendingPageTag;
    private System.Drawing.Icon? _windowIcon;

    public event EventHandler? Exiting;

    public event Action<AppPreferences>? PreferencesApplied;

    public MainWindow(
        ActivityTrackingService trackingService,
        IActivityStore activityStore,
        ApplicationSettingsService settingsService,
        IStartupService startupService,
        AppPreferences preferences,
        string dataDirectory)
    {
        _trackingService = trackingService;
        _activityStore = activityStore;
        _settingsService = settingsService;
        _startupService = startupService;
        _preferences = preferences;
        _dataDirectory = dataDirectory;
        InitializeComponent();
        Title = "Sika Time Tracker";
        ApplyWindowIcon();
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1180, 760));
        _isChangingSelection = true;
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        _isChangingSelection = false;
        ApplyTheme(_preferences.Theme);
        _pageSwitchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _pageSwitchTimer.Tick += OnPageSwitchTimerTick;
        ApplyPageSwitch("activity");
        _trackingService.StatusChanged += OnTrackingStatusChanged;
        _trackingService.ActivityRecorded += OnActivityRecorded;
        _trackingStatusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _trackingStatusTimer.Tick += OnTrackingStatusTimerTick;
        _trackingStatusTimer.Start();
        AppWindow.Closing += OnWindowClosing;
        Activated += OnWindowActivated;
    }

    private void OnTrackingStatusChanged(object? sender, TrackingStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _lastTrackingStatus = status;
            UpdateTrackingStatus(status);
        });
    }

    private void OnTrackingStatusTimerTick(object? sender, object args)
    {
        if (_isWindowVisible && _lastTrackingStatus is not null)
        {
            UpdateTrackingStatus(_lastTrackingStatus);
        }
    }

    private void UpdateTrackingStatus(TrackingStatus status)
    {
        StatusText.Text = status.StatusText;
        StatusIndicator.Fill = RootLayout.Resources[GetStatusBrushKey(status)] as Brush;

        if (status.IsTracking && status.CurrentWindow is not null)
        {
            TrackedProcessText.Text = status.CurrentWindow.ProcessName;
            var trackedContext = string.IsNullOrWhiteSpace(status.CurrentWindow.WebsiteDomain)
                ? status.CurrentWindow.WindowTitle
                : string.IsNullOrWhiteSpace(status.CurrentWindow.WindowTitle)
                    ? status.CurrentWindow.WebsiteDomain
                    : $"{status.CurrentWindow.WebsiteDomain} · {status.CurrentWindow.WindowTitle}";
            TrackedWindowText.Text = trackedContext;
            TrackedWindowText.Visibility = string.IsNullOrWhiteSpace(trackedContext)
                                           || string.Equals(
                                               status.CurrentWindow.ProcessName,
                                               trackedContext,
                                               StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed
                : Visibility.Visible;
            TrackingDurationText.Text = status.CurrentActivityStartedAtUtc is { } startedAt
                ? $"本次 {FormatDuration(DateTimeOffset.UtcNow - startedAt)}"
                : string.Empty;
            return;
        }

        TrackingDurationText.Text = string.Empty;
        TrackedWindowText.Visibility = Visibility.Visible;
        if (!status.IsSystemInteractive)
        {
            TrackedProcessText.Text = "已停止计时";
            TrackedWindowText.Text = "唤醒或解锁后自动恢复";
        }
        else if (status.IsIdle)
        {
            TrackedProcessText.Text = "已停止计时";
            TrackedWindowText.Text = "检测到电脑无人操作";
        }
        else if (status.IsPaused)
        {
            TrackedProcessText.Text = "配置更新中";
            TrackedWindowText.Text = "完成后自动恢复追踪";
        }
        else if (status.ForegroundWindow is { } foreground
                 && ProcessExclusionPolicy.ShouldExclude(foreground.ProcessName))
        {
            TrackedProcessText.Text = "当前窗口不计入";
            TrackedWindowText.Text = foreground.ProcessName;
        }
        else
        {
            TrackedProcessText.Text = "等待可记录窗口";
            TrackedWindowText.Text = status.ForegroundWindow?.ProcessName ?? "暂无前台窗口";
        }
    }

    private static string GetStatusBrushKey(TrackingStatus status)
    {
        if (status.IsTracking)
        {
            return "TrackingActiveBrush";
        }

        if (status.IsIdle)
        {
            return "TrackingAfkBrush";
        }

        return !status.IsSystemInteractive || status.IsPaused
            ? "TrackingInactiveBrush"
            : "TrackingReadyBrush";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private void OnActivityRecorded(object? sender, EventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_currentPage is ActivityView activity)
            {
                activity.RequestRefresh();
            }
        });
    }

    public void ShowFromTray()
    {
        _isWindowVisible = true;
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        SetWindowActive(true);
        if (_lastTrackingStatus is not null)
        {
            UpdateTrackingStatus(_lastTrackingStatus);
        }
    }

    public void Maximize()
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    public bool IsForeground()
    {
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return _isWindowVisible
            && GetForegroundWindow() == windowHandle
            && !IsIconic(windowHandle);
    }

    public void ToggleFromTaskbarBadge(bool wasForegroundBeforeClick)
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            if (_isWindowVisible
                && wasForegroundBeforeClick
                && presenter.State != Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            {
                ShowWindow(
                    WinRT.Interop.WindowNative.GetWindowHandle(this),
                    ShowWindowCommand.Minimize);
                SetWindowActive(false);
                return;
            }

            if (presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }
        }

        ShowFromTray();
    }

    public void HideToTray()
    {
        _isWindowVisible = false;
        SetWindowActive(false);
        AppWindow.Hide();
    }

    public void ApplyPreferences(AppPreferences preferences)
    {
        _preferences = preferences;
        ApplyTheme(preferences.Theme);
        PreferencesApplied?.Invoke(preferences);
    }

    public async Task ExitAsync()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        _trackingStatusTimer.Stop();
        _trackingStatusTimer.Tick -= OnTrackingStatusTimerTick;
        _trackingService.StatusChanged -= OnTrackingStatusChanged;
        _trackingService.ActivityRecorded -= OnActivityRecorded;
        await _trackingService.DisposeAsync();
        _windowIcon?.Dispose();
        _windowIcon = null;
        Exiting?.Invoke(this, EventArgs.Empty);
        Application.Current.Exit();
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            return;
        }

        args.Cancel = true;
        HideToTray();
    }

    private async void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isChangingSelection
            || args.SelectedItemContainer?.Tag is not string tag
            || tag == _currentTag)
        {
            return;
        }

        if (_currentPage is SettingsView settings && !await settings.ConfirmNavigationAsync())
        {
            _isChangingSelection = true;
            RootNavigation.SelectedItem = RootNavigation.MenuItems
                .OfType<NavigationViewItem>()
                .First(item => string.Equals(item.Tag as string, _currentTag, StringComparison.Ordinal));
            _isChangingSelection = false;
            return;
        }

        ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        _pendingPageTag = tag;
        _pageSwitchTimer.Stop();
        _pageSwitchTimer.Start();
    }

    private void OnPageSwitchTimerTick(object? sender, object args)
    {
        _pageSwitchTimer.Stop();
        if (_pendingPageTag is null
            || string.Equals(_pendingPageTag, _currentTag, StringComparison.Ordinal))
        {
            return;
        }

        var tag = _pendingPageTag;
        _pendingPageTag = null;
#if DEBUG
        PerfDiagnostics.Log($"SwitchTimer fired, switching to {tag}");
#endif
        ApplyPageSwitch(tag);
    }

    private void ApplyPageSwitch(string tag)
    {
#if DEBUG
        var switchStopwatch = System.Diagnostics.Stopwatch.StartNew();
#endif
        if (_currentPage is ActivityView previousActivity)
        {
            previousActivity.SetHostActive(false);
        }

        if (!_pageCache.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "rules" => new RulesView(_activityStore, _trackingService),
                "settings" => new SettingsView(
                    _activityStore,
                    _settingsService,
                    _startupService,
                    _trackingService,
                    _preferences,
                    _dataDirectory,
                    ApplyPreferences),
                _ => new ActivityView(
                    _activityStore,
                    _trackingService,
                    TimeSpan.FromSeconds(_preferences.MinimumActivitySeconds))
            };
            _pageCache[tag] = page;
            ContentHost.Children.Add(page);
            page.Visibility = Visibility.Collapsed;
        }

        if (_currentPage is { } current)
        {
            current.Visibility = Visibility.Collapsed;
        }

        _currentTag = tag;
        _currentPage = page;
        page.Visibility = Visibility.Visible;
        if (page is ActivityView activity)
        {
            activity.SetHostActive(_isWindowActive);
        }
#if DEBUG
        PerfDiagnostics.Log($"ApplyPageSwitch({tag}): {switchStopwatch.ElapsedMilliseconds}ms");
#endif
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        SetWindowActive(args.WindowActivationState != WindowActivationState.Deactivated);
    }

    private void SetWindowActive(bool isActive)
    {
        _isWindowActive = isActive;
        if (_currentPage is ActivityView activity)
        {
            activity.SetHostActive(isActive);
        }
    }

    private void ApplyTheme(AppTheme theme)
    {
        RootNavigation.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void ApplyWindowIcon()
    {
        _ = ApplyWindowIconAsync();
    }

    private async Task ApplyWindowIconAsync()
    {
        _windowIcon?.Dispose();
        _windowIcon = null;
        string? iconPath = null;
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            iconPath = Path.Combine(_dataDirectory, "SikaTimeTracker.ico");
            using var iconResource = typeof(MainWindow).Assembly.GetManifestResourceStream(
                WindowIconResourceName);
            if (iconResource is not null
                && (!File.Exists(iconPath) || new FileInfo(iconPath).Length != iconResource.Length))
            {
                await using var iconFile = new FileStream(iconPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                await iconResource.CopyToAsync(iconFile);
            }

            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
                _windowIcon = new System.Drawing.Icon(iconPath);
            }
        }
        catch
        {
            iconPath = null;
        }

        _windowIcon ??= string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? null
            : System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        if (_windowIcon is null)
        {
            return;
        }

        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SendMessage(windowHandle, WindowMessageSetIcon, SmallIcon, _windowIcon.Handle);
        SendMessage(windowHandle, WindowMessageSetIcon, BigIcon, _windowIcon.Handle);
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint windowHandle, uint message, nuint parameter, nint value);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, ShowWindowCommand command);

    private enum ShowWindowCommand
    {
        Minimize = 6
    }
}

internal static class PerfDiagnostics
{
    public static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SikaTimeTracker");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "perf.log"),
                line + Environment.NewLine);
        }
        catch
        {
        }
    }
}
