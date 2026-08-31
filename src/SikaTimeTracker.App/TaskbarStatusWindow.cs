using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
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
    private const string BadgeManualPositionSettingKey = "BadgeManualPosition";
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
    private bool _isManuallyPositioned;
    private bool _isDraggingBadge;
    private System.Drawing.Point _manualPosition;
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
        _form.SingleClicked += OnBadgeSingleClicked;
        _form.DoubleClicked += OnBadgeDoubleClicked;
        _form.DragStarted += OnBadgeDragStarted;
        _form.DragEnded += OnBadgeDragEnded;
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
        _ = LoadManualPositionAsync();
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
        _form.SingleClicked -= OnBadgeSingleClicked;
        _form.DoubleClicked -= OnBadgeDoubleClicked;
        _form.DragStarted -= OnBadgeDragStarted;
        _form.DragEnded -= OnBadgeDragEnded;
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

    private void OnBadgeSingleClicked()
    {
        _toggleMainWindow(_wasMainWindowForegroundOnMouseDown);
        _wasMainWindowForegroundOnMouseDown = false;
    }

    private void OnBadgeDoubleClicked()
    {
        // 双击恢复自动贴回任务栏
        _isManuallyPositioned = false;
        _ = ClearManualPositionAsync();
        UpdatePosition();
    }

    private void OnBadgeDragStarted()
    {
        _isDraggingBadge = true;
    }

    private void OnBadgeDragEnded(System.Drawing.Point location)
    {
        _isDraggingBadge = false;
        _manualPosition = location;
        _isManuallyPositioned = true;
        _ = SaveManualPositionAsync(location);
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        // 拖动期间不自动定位：窗口位置变化触发的 WinEvent 会回调 UpdatePosition，
        // 若此时贴回任务栏会导致拖动被打断
        if (_isDraggingBadge)
        {
            return;
        }

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

        if (_isManuallyPositioned)
        {
            // 手动位置：停靠在用户放置处，不贴回任务栏（尺寸沿用当前值）
            var position = ClampToWorkingArea(_manualPosition, _form.Width, _form.Height);
            _isVisible = TaskbarNativeService.PlaceAndShow(
                _windowHandle,
                taskbar.TaskbarWindowHandle,
                new TaskbarBadgePlacement(position.X, position.Y, _form.Width, _form.Height, _isCompact));
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

    private static System.Drawing.Point ClampToWorkingArea(
        System.Drawing.Point desired,
        int width,
        int height)
    {
        // 用整个屏幕 Bounds（含任务栏占位），手动位置可放到任务栏区域/屏幕边缘
        var bounds = Forms.Screen.FromPoint(desired).Bounds;
        var x = Math.Clamp(desired.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - width));
        var y = Math.Clamp(desired.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - height));
        return new System.Drawing.Point(x, y);
    }

    private async Task SaveManualPositionAsync(System.Drawing.Point location)
    {
        try
        {
            await _store.SetSettingAsync(
                BadgeManualPositionSettingKey,
                $"{location.X},{location.Y},{_form.Width},{_form.Height}");
        }
        catch
        {
        }
    }

    private async Task ClearManualPositionAsync()
    {
        try
        {
            await _store.SetSettingAsync(BadgeManualPositionSettingKey, string.Empty);
        }
        catch
        {
        }
    }

    private async Task LoadManualPositionAsync()
    {
        try
        {
            var value = await _store.GetSettingAsync(BadgeManualPositionSettingKey);
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var parts = value.Split(',');
            if (parts.Length >= 4
                && int.TryParse(parts[0], out var x)
                && int.TryParse(parts[1], out var y)
                && int.TryParse(parts[2], out var width)
                && int.TryParse(parts[3], out var height))
            {
                _manualPosition = ClampToWorkingArea(
                    new System.Drawing.Point(x, y),
                    Math.Max(1, width),
                    Math.Max(1, height));
                _isManuallyPositioned = true;
                UpdatePosition();
            }
        }
        catch
        {
        }
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
    private const int DragThresholdPixels = 4;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private string _durationText = "计算中";
    private bool _isCompact;
    private bool _useLightPalette;
    private bool _isHovered;
    private bool _isPressed;
    private bool _mouseDownLeft;
    private bool _isDragging;
    private bool _suppressClick;
    private System.Drawing.Point _mouseDownScreen;
    private System.Drawing.Point _windowStartLocation;
    private DateTime _lastMouseDownUtc;
    private System.Drawing.Point _lastMouseDownScreen;
    private DateTime _lastDragLogUtc;
    private int _dragLogCount;
    private readonly Forms.Timer _clickDelayTimer;

    public event Action<System.Drawing.Point>? DragEnded;

    public event Action? DragStarted;

    public event Action? SingleClicked;

    public event Action? DoubleClicked;

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
        _clickDelayTimer = new Forms.Timer { Interval = Forms.SystemInformation.DoubleClickTime };
        _clickDelayTimer.Tick += OnClickDelayTimerTick;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string DurationText
    {
        get => _durationText;
        set
        {
            _durationText = value;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            _isCompact = value;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
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
        PerfDiagnostics.Log($"Badge: MouseDown button={eventArgs.Button} loc={eventArgs.Location} win={Location}");
        base.OnMouseDown(eventArgs);
        if (eventArgs.Button != Forms.MouseButtons.Left)
        {
            return;
        }

        _mouseDownLeft = true;
        _mouseDownScreen = Forms.Cursor.Position;
        _windowStartLocation = Location;
        _isDragging = false;
        Capture = true;

        // 双击检测（系统双击时间与距离阈值）
        var now = DateTime.UtcNow;
        var isDoubleClick = (now - _lastMouseDownUtc).TotalMilliseconds <= Forms.SystemInformation.DoubleClickTime
                            && Math.Abs(_mouseDownScreen.X - _lastMouseDownScreen.X) <= Forms.SystemInformation.DoubleClickSize.Width
                            && Math.Abs(_mouseDownScreen.Y - _lastMouseDownScreen.Y) <= Forms.SystemInformation.DoubleClickSize.Height;
        _lastMouseDownUtc = now;
        _lastMouseDownScreen = _mouseDownScreen;
        if (isDoubleClick)
        {
            _clickDelayTimer.Stop();
            _suppressClick = true;
            DoubleClicked?.Invoke();
        }
        else
        {
            // 关键：普通按下重置抑制标志——上次拖动/双击留下的 true 会卡死后续拖动
            _suppressClick = false;
        }

        _isPressed = true;
        UpdateInteractionAppearance();
    }

    protected override void OnMouseMove(Forms.MouseEventArgs eventArgs)
    {
        // 注意：入口只检查按下状态，不能用 _suppressClick——
        // 拖动移动中 _suppressClick 会被置 true，若作为入口条件会导致拖动只动一帧就停
        if (_mouseDownLeft)
        {
            var deltaX = Forms.Cursor.Position.X - _mouseDownScreen.X;
            var deltaY = Forms.Cursor.Position.Y - _mouseDownScreen.Y;
            if (!_isDragging
                && (Math.Abs(deltaX) > DragThresholdPixels || Math.Abs(deltaY) > DragThresholdPixels))
            {
                _isDragging = true;
                _isHovered = false;
                _dragLogCount = 0;
                PerfDiagnostics.Log($"Badge: DragStarted start={_windowStartLocation}");
                DragStarted?.Invoke();
            }

            if (_isDragging)
            {
                // 拖动：跟随鼠标并约束在光标所在屏幕的工作区内（多屏适配）
                // 用原生 SetWindowPos 移动（绕过 WinForms Location setter），再同步边界缓存
                var target = ClampToWorkingArea(new System.Drawing.Point(
                    _windowStartLocation.X + deltaX,
                    _windowStartLocation.Y + deltaY));
                SetWindowPos(
                    Handle,
                    nint.Zero,
                    target.X,
                    target.Y,
                    0,
                    0,
                    SetWindowPositionNoSize | SetWindowPositionNoZOrder | SetWindowPositionNoActivate);
                UpdateBounds(target.X, target.Y, Width, Height);
                _suppressClick = true;
                var now = DateTime.UtcNow;
                if (_dragLogCount < 30
                    && (now - _lastDragLogUtc).TotalMilliseconds >= 150)
                {
                    _lastDragLogUtc = now;
                    _dragLogCount++;
                    PerfDiagnostics.Log($"Badge: drag move -> {Location}");
                }

                return;
            }
        }

        base.OnMouseMove(eventArgs);
    }

    protected override void OnMouseUp(Forms.MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        if (eventArgs.Button != Forms.MouseButtons.Left || !_mouseDownLeft)
        {
            return;
        }

        _mouseDownLeft = false;
        Capture = false;
        if (_isDragging)
        {
            _isDragging = false;
            _suppressClick = true;
            PerfDiagnostics.Log($"Badge: DragEnded at {Location}");
            DragEnded?.Invoke(Location);
        }
        else if (!_suppressClick)
        {
            // 单击候选：延迟到双击窗口结束再触发，避免与双击冲突
            _clickDelayTimer.Stop();
            _clickDelayTimer.Start();
        }

        _isPressed = false;
        UpdateInteractionAppearance();
    }

    protected override void OnMouseCaptureChanged(EventArgs eventArgs)
    {
        base.OnMouseCaptureChanged(eventArgs);
        if (!Capture)
        {
            if (_isDragging)
            {
                // 拖动中捕获丢失：立即重新捕获，避免拖动中断
                Capture = true;
                return;
            }

            _mouseDownLeft = false;
            _isDragging = false;
            if (_isPressed)
            {
                _isPressed = false;
                UpdateInteractionAppearance();
            }
        }
    }

    protected override void OnFormClosing(Forms.FormClosingEventArgs eventArgs)
    {
        base.OnFormClosing(eventArgs);
        _clickDelayTimer.Stop();
        _clickDelayTimer.Tick -= OnClickDelayTimerTick;
        _clickDelayTimer.Dispose();
    }

    private void OnClickDelayTimerTick(object? sender, EventArgs eventArgs)
    {
        _clickDelayTimer.Stop();
        if (!_suppressClick)
        {
            SingleClicked?.Invoke();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    private System.Drawing.Point ClampToWorkingArea(System.Drawing.Point desired)
    {
        // 用整个屏幕 Bounds（含任务栏占位），允许拖到任务栏区域/屏幕边缘
        var bounds = Forms.Screen.FromPoint(Forms.Cursor.Position).Bounds;
        var x = Math.Clamp(desired.X, bounds.Left, Math.Max(bounds.Left, bounds.Right - Width));
        var y = Math.Clamp(desired.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - Height));
        return new System.Drawing.Point(x, y);
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
