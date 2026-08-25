using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;
using SikaTimeTracker.Services;
using Forms = System.Windows.Forms;

namespace SikaTimeTracker;

public sealed class TaskbarStatusWindow : IDisposable
{
    private readonly IActivityStore _store;
    private readonly ActivityTrackingService _trackingService;
    private readonly Func<bool> _isMainWindowForeground;
    private readonly Action<bool> _toggleMainWindow;
    private readonly WeeklyWorkSummaryService _summaryService = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _summaryTimer;
    private readonly DispatcherQueueTimer _environmentSettleTimer;
    private readonly IDisposable _environmentMonitor;
    private readonly TaskbarBadgeForm _form;
    private readonly Forms.ToolTip _toolTip = new();
    private readonly nint _windowHandle;
    private WeeklyWorkSummary? _summary;
    private TimeSpan _minimumActivityDuration;
    private bool _isCompact;
    private bool _isRefreshing;
    private bool _isVisible;
    private bool _disposed;
    private bool _wasMainWindowForegroundOnMouseDown;
    private int _positionUpdateQueued;

    public TaskbarStatusWindow(
        IActivityStore store,
        ActivityTrackingService trackingService,
        TimeSpan minimumActivityDuration,
        AppTheme theme,
        Func<bool> isMainWindowForeground,
        Action<bool> toggleMainWindow)
    {
        _store = store;
        _trackingService = trackingService;
        _minimumActivityDuration = minimumActivityDuration;
        _isMainWindowForeground = isMainWindowForeground;
        _toggleMainWindow = toggleMainWindow;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _form = new TaskbarBadgeForm
        {
            Text = "Sika Time Tracker 本周工作时长",
            AccessibleName = "本周工作时长"
        };
        _form.MouseDown += OnBadgeMouseDown;
        _form.Click += OnBadgeClicked;
        ApplyTheme(theme);
        _windowHandle = _form.Handle;
        TaskbarNativeService.ConfigureToolWindow(_windowHandle);
        _environmentMonitor = TaskbarNativeService.WatchEnvironment(OnTaskbarEnvironmentChanged);
        _environmentSettleTimer = _dispatcherQueue.CreateTimer();
        _environmentSettleTimer.Interval = TimeSpan.FromMilliseconds(120);
        _environmentSettleTimer.IsRepeating = false;
        _environmentSettleTimer.Tick += OnEnvironmentSettleTimerTick;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
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
        _form.UseLightPalette = theme == AppTheme.Light;
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
        _environmentSettleTimer.Stop();
        _positionTimer.Tick -= OnPositionTimerTick;
        _summaryTimer.Tick -= OnSummaryTimerTick;
        _environmentSettleTimer.Tick -= OnEnvironmentSettleTimerTick;
        _trackingService.ActivityRecorded -= OnActivityRecorded;
        _environmentMonitor.Dispose();
        _form.MouseDown -= OnBadgeMouseDown;
        _form.Click -= OnBadgeClicked;
        TaskbarNativeService.Hide(_windowHandle);
        _toolTip.Dispose();
        _form.Close();
        _form.Dispose();
    }

    private void OnPositionTimerTick(object? sender, object args)
    {
        UpdatePosition();
    }

    private void OnTaskbarEnvironmentChanged()
    {
        if (_disposed || Interlocked.Exchange(ref _positionUpdateQueued, 1) != 0)
        {
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _positionUpdateQueued, 0);
                if (!_disposed)
                {
                    UpdatePosition();
                    _environmentSettleTimer.Stop();
                    _environmentSettleTimer.Start();
                }
            }))
        {
            Interlocked.Exchange(ref _positionUpdateQueued, 0);
        }
    }

    private void OnEnvironmentSettleTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!_disposed)
        {
            UpdatePosition();
        }
    }

    private async void OnSummaryTimerTick(object? sender, object args)
    {
        await RefreshSummaryAsync();
    }

    private void OnActivityRecorded(object? sender, EventArgs args)
    {
        _dispatcherQueue.TryEnqueue(async () => await RefreshSummaryAsync());
    }

    private void OnBadgeMouseDown(object? sender, Forms.MouseEventArgs args)
    {
        if (args.Button == Forms.MouseButtons.Left)
        {
            _wasMainWindowForegroundOnMouseDown = _isMainWindowForeground();
        }
    }

    private void OnBadgeClicked(object? sender, EventArgs args)
    {
        _toggleMainWindow(_wasMainWindowForegroundOnMouseDown);
        _wasMainWindowForegroundOnMouseDown = false;
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

        _isVisible = TaskbarNativeService.PlaceAndShow(
            _windowHandle,
            taskbar.TaskbarWindowHandle,
            placement);
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
            var summary = _summaryService.Calculate(
                activities,
                categories,
                nowUtc,
                TimeZoneInfo.Local,
                _minimumActivityDuration);
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _summary = summary;
                UpdateSummaryPresentation();
            });
        }
        catch
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _form.DurationText = "--";
                _form.AccessibleName = "本周工作时长暂时不可用";
                _toolTip.SetToolTip(_form, "暂时无法读取本周工作时长；点击打开或最小化 Sika Time Tracker");
            });
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

        _form.IsCompact = _isCompact;
        var fullDuration = FormatFullDuration(_summary.Duration);
        _form.DurationText = _summary.HasWorkCategory
            ? _isCompact ? FormatCompactDuration(_summary.Duration) : fullDuration
            : _isCompact ? "--" : "未设置";
        var description = _summary.HasWorkCategory
            ? $"本周工作时长：{fullDuration}"
            : "尚未创建名为“工作”的分类";
        _form.AccessibleName = description;
        _toolTip.SetToolTip(_form, $"{description}\n点击打开或最小化 Sika Time Tracker");
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

internal sealed class TaskbarBadgeForm : Forms.Form
{
    private const int ExtendedStyleNoActivate = 0x08000000;
    private const int ExtendedStyleToolWindow = 0x00000080;
    private const int WindowMessageMouseActivate = 0x0021;
    private const int MouseActivateNoActivate = 3;
    private string _durationText = "计算中";
    private bool _isCompact;
    private bool _useLightPalette;
    private bool _isHovered;
    private bool _isPressed;

    public TaskbarBadgeForm()
    {
        AutoScaleMode = Forms.AutoScaleMode.None;
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        TopMost = true;
        Opacity = RestingOpacity;
        Cursor = Forms.Cursors.Hand;
        AccessibleRole = Forms.AccessibleRole.PushButton;
        BackColor = DarkBackground;
        SetStyle(
            Forms.ControlStyles.AllPaintingInWmPaint
            | Forms.ControlStyles.OptimizedDoubleBuffer
            | Forms.ControlStyles.ResizeRedraw
            | Forms.ControlStyles.UserPaint,
            true);
    }

    public string DurationText
    {
        get => _durationText;
        set
        {
            _durationText = value;
            Invalidate();
        }
    }

    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            _isCompact = value;
            Invalidate();
        }
    }

    public bool UseLightPalette
    {
        get => _useLightPalette;
        set
        {
            _useLightPalette = value;
            BackColor = BackgroundColor;
            Invalidate();
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= ExtendedStyleNoActivate | ExtendedStyleToolWindow;
            return parameters;
        }
    }

    protected override void WndProc(ref Forms.Message message)
    {
        if (message.Msg == WindowMessageMouseActivate)
        {
            message.Result = MouseActivateNoActivate;
            return;
        }

        base.WndProc(ref message);
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        var previousRegion = Region;
        Region = CreateRoundedRegion(ClientRectangle, Math.Max(8, Height / 3));
        previousRegion?.Dispose();
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        base.OnMouseEnter(eventArgs);
        _isHovered = true;
        UpdateInteractionAppearance();
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        base.OnMouseLeave(eventArgs);
        _isHovered = false;
        _isPressed = false;
        UpdateInteractionAppearance();
    }

    protected override void OnMouseDown(Forms.MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button == Forms.MouseButtons.Left)
        {
            _isPressed = true;
            UpdateInteractionAppearance();
        }
    }

    protected override void OnMouseUp(Forms.MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (_isPressed)
        {
            _isPressed = false;
            UpdateInteractionAppearance();
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs eventArgs)
    {
        base.OnMouseCaptureChanged(eventArgs);
        if (!Capture && _isPressed)
        {
            _isPressed = false;
            UpdateInteractionAppearance();
        }
    }

    protected override void OnPaint(Forms.PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var secondaryText = _useLightPalette
            ? Color.FromArgb(92, 92, 92)
            : Color.FromArgb(210, 210, 210);
        var primaryText = _useLightPalette
            ? Color.FromArgb(24, 24, 24)
            : Color.White;
        eventArgs.Graphics.Clear(BackgroundColor);

        using var labelFont = new Font("Segoe UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point);
        using var durationFont = new Font("Segoe UI", 9.75f, FontStyle.Bold, GraphicsUnit.Point);
        var flags = Forms.TextFormatFlags.NoPadding
                    | Forms.TextFormatFlags.NoPrefix
                    | Forms.TextFormatFlags.SingleLine
                    | Forms.TextFormatFlags.VerticalCenter;
        if (_isCompact)
        {
            Forms.TextRenderer.DrawText(
                eventArgs.Graphics,
                _durationText,
                durationFont,
                ClientRectangle,
                primaryText,
                flags | Forms.TextFormatFlags.HorizontalCenter);
            return;
        }

        const string label = "本周工作";
        var labelSize = Forms.TextRenderer.MeasureText(eventArgs.Graphics, label, labelFont, Size.Empty, flags);
        var durationSize = Forms.TextRenderer.MeasureText(
            eventArgs.Graphics,
            _durationText,
            durationFont,
            Size.Empty,
            flags);
        var gap = Math.Max(6, (int)Math.Round(DeviceDpi / 96d * 7));
        var contentWidth = labelSize.Width + gap + durationSize.Width;
        var left = Math.Max(0, (ClientSize.Width - contentWidth) / 2);
        Forms.TextRenderer.DrawText(
            eventArgs.Graphics,
            label,
            labelFont,
            new Rectangle(left, 0, labelSize.Width, ClientSize.Height),
            secondaryText,
            flags);
        Forms.TextRenderer.DrawText(
            eventArgs.Graphics,
            _durationText,
            durationFont,
            new Rectangle(left + labelSize.Width + gap, 0, durationSize.Width, ClientSize.Height),
            primaryText,
            flags);
    }

    private static Region CreateRoundedRegion(Rectangle bounds, int radius)
    {
        using var path = CreateRoundedPath(bounds, radius);
        return new Region(path);
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static readonly Color DarkBackground = Color.FromArgb(55, 55, 55);

    private static readonly Color DarkHoverBackground = Color.FromArgb(70, 70, 70);

    private static readonly Color DarkPressedBackground = Color.FromArgb(45, 45, 45);

    private static readonly Color LightBackground = Color.FromArgb(242, 242, 242);

    private static readonly Color LightHoverBackground = Color.FromArgb(228, 228, 228);

    private static readonly Color LightPressedBackground = Color.FromArgb(214, 214, 214);

    private const double RestingOpacity = 0.78;

    private const double HoverOpacity = 0.9;

    private const double PressedOpacity = 0.82;

    private Color BackgroundColor => (_useLightPalette, _isPressed, _isHovered) switch
    {
        (true, true, _) => LightPressedBackground,
        (true, false, true) => LightHoverBackground,
        (true, false, false) => LightBackground,
        (false, true, _) => DarkPressedBackground,
        (false, false, true) => DarkHoverBackground,
        _ => DarkBackground
    };

    private double SurfaceOpacity => (_isPressed, _isHovered) switch
    {
        (true, _) => PressedOpacity,
        (false, true) => HoverOpacity,
        _ => RestingOpacity
    };

    private void UpdateInteractionAppearance()
    {
        BackColor = BackgroundColor;
        Opacity = SurfaceOpacity;
        Invalidate();
    }
}
