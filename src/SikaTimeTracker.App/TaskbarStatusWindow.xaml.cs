using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;
using SikaTimeTracker.Services;

namespace SikaTimeTracker;

public sealed partial class TaskbarStatusWindow : Window, IDisposable
{
    private readonly IActivityStore _store;
    private readonly ActivityTrackingService _trackingService;
    private readonly Action _openMainWindow;
    private readonly WeeklyWorkSummaryService _summaryService = new();
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _summaryTimer;
    private readonly nint _windowHandle;
    private WeeklyWorkSummary? _summary;
    private TimeSpan _minimumActivityDuration;
    private bool _isCompact;
    private bool _isRefreshing;
    private bool _isVisible;
    private bool _disposed;

    public TaskbarStatusWindow(
        IActivityStore store,
        ActivityTrackingService trackingService,
        TimeSpan minimumActivityDuration,
        AppTheme theme,
        Action openMainWindow)
    {
        _store = store;
        _trackingService = trackingService;
        _minimumActivityDuration = minimumActivityDuration;
        _openMainWindow = openMainWindow;
        InitializeComponent();
        Title = "Sika Time Tracker 本周工作时长";
        ApplyTheme(theme);
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ConfigureWindow();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _positionTimer.Tick += OnPositionTimerTick;
        _summaryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _summaryTimer.Tick += OnSummaryTimerTick;
        _trackingService.ActivityRecorded += OnActivityRecorded;
        _positionTimer.Start();
        _summaryTimer.Start();
        UpdatePosition();
        _ = RefreshSummaryAsync();
    }

    public void ApplyTheme(AppTheme theme)
    {
        BadgeRoot.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    public void ApplyPreferences(AppPreferences preferences)
    {
        _minimumActivityDuration = TimeSpan.FromSeconds(preferences.MinimumActivitySeconds);
        ApplyTheme(preferences.Theme);
        _ = RefreshSummaryAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _positionTimer.Stop();
        _summaryTimer.Stop();
        _positionTimer.Tick -= OnPositionTimerTick;
        _summaryTimer.Tick -= OnSummaryTimerTick;
        _trackingService.ActivityRecorded -= OnActivityRecorded;
        TaskbarNativeService.Hide(_windowHandle);
        Close();
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        TaskbarNativeService.ConfigureToolWindow(_windowHandle);
    }

    private void OnPositionTimerTick(object? sender, object args)
    {
        UpdatePosition();
    }

    private async void OnSummaryTimerTick(object? sender, object args)
    {
        await RefreshSummaryAsync();
    }

    private void OnActivityRecorded(object? sender, EventArgs args)
    {
        DispatcherQueue.TryEnqueue(async () => await RefreshSummaryAsync());
    }

    private void OnBadgeTapped(object sender, TappedRoutedEventArgs args)
    {
        _openMainWindow();
    }

    private void UpdatePosition()
    {
        if (!TaskbarNativeService.TryGetTaskbarState(out var taskbar)
            || taskbar.IsTemporarilyHidden
            || TaskbarNativeService.IsFullscreenWindowOnTaskbarMonitor(taskbar, _windowHandle))
        {
            if (_isVisible)
            {
                TaskbarNativeService.Hide(_windowHandle);
                _isVisible = false;
            }

            return;
        }

        var placement = TaskbarBadgeLayoutCalculator.Calculate(
            taskbar.TaskbarBounds,
            taskbar.Edge,
            taskbar.Dpi);
        if (_isCompact != placement.IsCompact)
        {
            _isCompact = placement.IsCompact;
            UpdateSummaryPresentation();
        }

        _isVisible = TaskbarNativeService.PlaceAndShow(_windowHandle, placement);
    }

    private async Task RefreshSummaryAsync()
    {
        if (_isRefreshing || _disposed)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var localNow = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Local);
            var today = DateOnly.FromDateTime(localNow.DateTime);
            var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
            var weekStart = today.AddDays(-daysSinceMonday);
            var (rangeStartUtc, _) = ActivityStatisticsService.GetDayBoundsUtc(weekStart, TimeZoneInfo.Local);
            var categories = await _store.GetCategoriesAsync();
            var activities = await _store.GetActivitiesAsync(rangeStartUtc, nowUtc);
            _summary = _summaryService.Calculate(
                activities,
                categories,
                nowUtc,
                TimeZoneInfo.Local,
                _minimumActivityDuration);
            UpdateSummaryPresentation();
        }
        catch
        {
            DurationText.Text = "--";
            ToolTipService.SetToolTip(BadgeRoot, "暂时无法读取本周工作时长；点击打开 Sika Time Tracker");
            AutomationProperties.SetName(BadgeRoot, "本周工作时长暂时不可用");
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void UpdateSummaryPresentation()
    {
        if (_summary is null)
        {
            return;
        }

        WeekLabel.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        BadgeRoot.Padding = _isCompact ? new Thickness(3, 0, 3, 0) : new Thickness(10, 0, 10, 0);
        DurationText.FontSize = _isCompact ? 11 : 13;
        var fullDuration = FormatFullDuration(_summary.Duration);
        DurationText.Text = _summary.HasWorkCategory
            ? _isCompact ? FormatCompactDuration(_summary.Duration) : fullDuration
            : _isCompact ? "--" : "未设置";
        var description = _summary.HasWorkCategory
            ? $"本周工作时长：{fullDuration}"
            : "尚未创建名为“工作”的分类";
        ToolTipService.SetToolTip(BadgeRoot, $"{description}\n点击打开 Sika Time Tracker");
        AutomationProperties.SetName(BadgeRoot, description);
    }

    private static string FormatFullDuration(TimeSpan duration)
    {
        var totalMinutes = Math.Max(0, (int)Math.Floor(duration.TotalMinutes));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours > 0 ? $"{hours}小时{minutes:00}分" : $"{minutes}分";
    }

    private static string FormatCompactDuration(TimeSpan duration)
    {
        var totalMinutes = Math.Max(0, (int)Math.Floor(duration.TotalMinutes));
        return totalMinutes >= 60 ? $"{totalMinutes / 60}h" : $"{totalMinutes}m";
    }
}
