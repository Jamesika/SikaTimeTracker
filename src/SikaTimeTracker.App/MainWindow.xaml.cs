using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
    private const double NavIndicatorHeight = 20;
    private const double NavIndicatorInset = 4;
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
    private Visual? _navIndicatorVisual;
    private Compositor? _navCompositor;
    private long _isPaneOpenToken;
    private MicaController? _micaController;
    private SystemBackdropConfiguration? _micaConfig;

    public event EventHandler? Exiting;

    public event Action<AppPreferences>? PreferencesApplied;

    public AppTheme CurrentTheme => _preferences.Theme;

    /// <summary>解析实际生效的主题（"跟随系统"时按窗口 ActualTheme 判定）。</summary>
    public bool IsDarkTheme => RootLayout.ActualTheme == ElementTheme.Dark;

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
        AppWindow.Resize(new SizeInt32(1180, 760));
        _isChangingSelection = true;
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        _isChangingSelection = false;
        ApplyTheme(_preferences.Theme);
        RootLayout.ActualThemeChanged += OnActualThemeChanged;
        _pageSwitchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _pageSwitchTimer.Tick += OnPageSwitchTimerTick;
        ApplyPageSwitch("activity");
        _navIndicatorVisual = ElementCompositionPreview.GetElementVisual(NavIndicator);
        _navCompositor = _navIndicatorVisual.Compositor;
        _isPaneOpenToken = RootNavigation.RegisterPropertyChangedCallback(
            NavigationView.IsPaneOpenProperty,
            OnIsPaneOpenChanged);
        DispatcherQueue.TryEnqueue(() => UpdateNavIndicator(_currentTag, animate: false));
        // Mica 背板延迟到窗口就绪后初始化（失败仅降级，不影响启动）
        DispatcherQueue.TryEnqueue(InitializeMica);
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
        _micaController?.Dispose();
        _micaController = null;
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

        UpdateNavIndicator(tag, animate: true);
        ShowPage(tag);
    }

    private void OnIsPaneOpenChanged(DependencyObject sender, DependencyProperty args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateNavIndicator(_currentTag, animate: false);
            // 导航收起时整块隐藏追踪状态，避免紧凑宽度下文字溢出错乱；展开时恢复
            TrackingStatusPanel.Visibility = RootNavigation.IsPaneOpen
                ? Visibility.Visible
                : Visibility.Collapsed;
        });
    }

    private void UpdateNavIndicator(string tag, bool animate)
    {
        if (_navIndicatorVisual is null || _navCompositor is null)
        {
            return;
        }

        var item = RootNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, tag, StringComparison.Ordinal));
        if (item is null || item.ActualHeight <= 0)
        {
            return;
        }

        var itemPosition = item.TransformToVisual(RootLayout).TransformPoint(new Windows.Foundation.Point(0, 0));
        var targetX = (float)(itemPosition.X + NavIndicatorInset);
        var targetY = (float)(itemPosition.Y + ((item.ActualHeight - NavIndicatorHeight) / 2));
        NavIndicator.Opacity = 1;

        if (!animate)
        {
            _navIndicatorVisual.Offset = new Vector3(targetX, targetY, 0);
            return;
        }

        // Composition 动画被新动画替换时从当前合成值平滑过渡到新目标：
        // 快速连续切换时指示条连续追踪选中项，不会跳变或被打断。
        var easing = _navCompositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        var animation = _navCompositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, new Vector3(targetX, targetY, 0), easing);
        animation.Duration = TimeSpan.FromMilliseconds(200);
        animation.Target = "Offset";
        _navIndicatorVisual.StartAnimation("Offset", animation);
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

        PlayPageFadeIn(page);
#if DEBUG
        PerfDiagnostics.Log($"ApplyPageSwitch({tag}): {switchStopwatch.ElapsedMilliseconds}ms");
#endif
    }

    private static void PlayPageFadeIn(FrameworkElement page)
    {
        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(150),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(fadeIn, page);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fadeIn);
        storyboard.Begin();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_micaConfig is not null)
        {
            _micaConfig.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;
        }

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
        // 窗口级主题：覆盖导航、内容页、指示条层等全部元素（Default 跟随系统）
        RootLayout.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        if (_micaConfig is not null)
        {
            _micaConfig.Theme = theme switch
            {
                AppTheme.Light => SystemBackdropTheme.Light,
                AppTheme.Dark => SystemBackdropTheme.Dark,
                _ => SystemBackdropTheme.Default
            };
        }

        ApplyTitleBarTheme(IsDarkTheme);
    }

    private bool _micaInitialized;

    private void InitializeMica()
    {
        if (_micaInitialized)
        {
            return;
        }

        _micaInitialized = true;
        try
        {
            if (!MicaController.IsSupported())
            {
                return;
            }

            _micaConfig = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = _preferences.Theme switch
                {
                    AppTheme.Light => SystemBackdropTheme.Light,
                    AppTheme.Dark => SystemBackdropTheme.Dark,
                    _ => SystemBackdropTheme.Default
                }
            };
            _micaController = new MicaController();
            _micaController.AddSystemBackdropTarget((ICompositionSupportsSystemBackdrop)RootLayout);
            _micaController.SetSystemBackdropConfiguration(_micaConfig);
        }
        catch
        {
            // Mica 初始化失败仅降级：无系统背板，窗口底色由 ApplicationPageBackgroundThemeBrush 兜底，绝不影响启动
            _micaController?.Dispose();
            _micaController = null;
            _micaConfig = null;
        }
    }

    private void ApplyTitleBarTheme(bool isDark)
    {
        try
        {
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var value = isDark ? 1 : 0;
            _ = DwmSetWindowAttribute(
                windowHandle,
                DwmwaUseImmersiveDarkMode,
                ref value,
                sizeof(int));
        }
        catch
        {
            // 句柄不可用时跳过标题栏主题设置
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        // "跟随系统"时系统主题变化，标题栏随之更新（Mica 的 Default 模式自动跟随）
        ApplyTitleBarTheme(IsDarkTheme);
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    private const int DwmwaUseImmersiveDarkMode = 20;

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
