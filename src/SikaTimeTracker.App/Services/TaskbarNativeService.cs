using System.Runtime.InteropServices;
using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Services;

internal static class TaskbarNativeService
{
    private const uint AppBarGetState = 0x00000004;
    private const uint AppBarGetTaskbarPosition = 0x00000005;
    private const nuint AppBarStateAutoHide = 0x00000001;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int WindowLongOwner = -8;
    private const int WindowLongExtendedStyle = -20;
    private const nint WindowStyleToolWindow = 0x00000080;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionShowWindow = 0x0040;
    private const int ShowWindowHide = 0;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowBorderColor = 34;
    private const int DwmWindowCornerRoundSmall = 3;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventSystemDesktopSwitch = 0x0020;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const int ObjectIdWindow = 0;
    private const uint WinEventOutOfContext = 0x0000;
    private static readonly nint TopmostWindow = new(-1);

    public static bool TryGetTaskbarState(out TaskbarState state)
    {
        var taskbarHandle = FindWindow("Shell_TrayWnd", null);
        var appBarData = new AppBarData
        {
            Size = (uint)Marshal.SizeOf<AppBarData>(),
            WindowHandle = taskbarHandle
        };
        if (taskbarHandle == nint.Zero
            || SHAppBarMessage(AppBarGetTaskbarPosition, ref appBarData) == 0)
        {
            state = default;
            return false;
        }

        var monitorHandle = MonitorFromWindow(taskbarHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitorHandle == nint.Zero || !GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            state = default;
            return false;
        }

        var dpi = GetDpiForWindow(taskbarHandle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var taskbarBounds = appBarData.Bounds.ToPixelBounds();
        var monitorBounds = monitorInfo.MonitorBounds.ToPixelBounds();
        var edge = Enum.IsDefined(typeof(TaskbarEdge), (int)appBarData.Edge)
            ? (TaskbarEdge)appBarData.Edge
            : TaskbarEdge.Bottom;
        var isAutoHide = (SHAppBarMessage(AppBarGetState, ref appBarData) & AppBarStateAutoHide) != 0;
        var isTemporarilyHidden = isAutoHide
                                  && GetWindowRect(taskbarHandle, out var actualBounds)
                                  && IsAutoHideTaskbarRetracted(edge, actualBounds.ToPixelBounds(), monitorBounds, dpi);
        state = new TaskbarState(
            taskbarHandle,
            monitorHandle,
            taskbarBounds,
            monitorBounds,
            edge,
            dpi,
            isTemporarilyHidden);
        return true;
    }

    public static bool IsFullscreenWindowOnTaskbarMonitor(TaskbarState state, nint badgeWindowHandle)
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == nint.Zero
            || foregroundWindow == badgeWindowHandle
            || foregroundWindow == state.TaskbarWindowHandle
            || IsIconic(foregroundWindow)
            || !IsWindowVisible(foregroundWindow)
            || MonitorFromWindow(foregroundWindow, MonitorDefaultToNearest) != state.MonitorHandle
            || !GetWindowRect(foregroundWindow, out var foregroundBounds))
        {
            return false;
        }

        var bounds = foregroundBounds.ToPixelBounds();
        const int tolerance = 2;
        return bounds.Left <= state.MonitorBounds.Left + tolerance
               && bounds.Top <= state.MonitorBounds.Top + tolerance
               && bounds.Right >= state.MonitorBounds.Right - tolerance
               && bounds.Bottom >= state.MonitorBounds.Bottom - tolerance;
    }

    public static void ConfigureToolWindow(nint windowHandle)
    {
        var extendedStyle = GetWindowLongPtr(windowHandle, WindowLongExtendedStyle);
        SetWindowLongPtr(windowHandle, WindowLongExtendedStyle, extendedStyle | WindowStyleToolWindow);
        var cornerPreference = DwmWindowCornerRoundSmall;
        _ = DwmSetWindowAttribute(
            windowHandle,
            DwmWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
        var borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(
            windowHandle,
            DwmWindowBorderColor,
            ref borderColor,
            sizeof(int));
    }

    public static bool PlaceAndShow(
        nint windowHandle,
        nint taskbarWindowHandle,
        TaskbarBadgePlacement placement)
    {
        _ = SetWindowLongPtr(windowHandle, WindowLongOwner, taskbarWindowHandle);
        return SetWindowPos(
            windowHandle,
            TopmostWindow,
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height,
            SetWindowPositionNoActivate | SetWindowPositionShowWindow);
    }

    public static void Hide(nint windowHandle)
    {
        _ = ShowWindow(windowHandle, ShowWindowHide);
    }

    public static IDisposable WatchEnvironment(Action changed)
    {
        return new TaskbarEnvironmentMonitor(changed);
    }

    private static bool IsAutoHideTaskbarRetracted(
        TaskbarEdge edge,
        PixelBounds actualBounds,
        PixelBounds monitorBounds,
        uint dpi)
    {
        var tolerance = Math.Max(2, (int)Math.Round(3 * dpi / 96d));
        return edge switch
        {
            TaskbarEdge.Left => actualBounds.Right <= monitorBounds.Left + tolerance,
            TaskbarEdge.Top => actualBounds.Bottom <= monitorBounds.Top + tolerance,
            TaskbarEdge.Right => actualBounds.Left >= monitorBounds.Right - tolerance,
            _ => actualBounds.Top >= monitorBounds.Bottom - tolerance
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint WindowHandle;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRectangle Bounds;
        public nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRectangle MonitorBounds;
        public NativeRectangle WorkBounds;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly PixelBounds ToPixelBounds() => new(Left, Top, Right, Bottom);
    }

    private sealed class TaskbarEnvironmentMonitor : IDisposable
    {
        private readonly Action _changed;
        private readonly WinEventCallback _callback;
        private readonly nint[] _hooks;
        private bool _disposed;

        public TaskbarEnvironmentMonitor(Action changed)
        {
            _changed = changed;
            _callback = OnWinEvent;
            _hooks =
            [
                SetWinEventHook(
                    EventSystemForeground,
                    EventSystemForeground,
                    nint.Zero,
                    _callback,
                    0,
                    0,
                    WinEventOutOfContext),
                SetWinEventHook(
                    EventSystemMinimizeStart,
                    EventSystemMinimizeEnd,
                    nint.Zero,
                    _callback,
                    0,
                    0,
                    WinEventOutOfContext),
                SetWinEventHook(
                    EventSystemDesktopSwitch,
                    EventSystemDesktopSwitch,
                    nint.Zero,
                    _callback,
                    0,
                    0,
                    WinEventOutOfContext),
                SetWinEventHook(
                    EventObjectCreate,
                    EventObjectHide,
                    nint.Zero,
                    _callback,
                    0,
                    0,
                    WinEventOutOfContext),
                SetWinEventHook(
                    EventObjectLocationChange,
                    EventObjectLocationChange,
                    nint.Zero,
                    _callback,
                    0,
                    0,
                    WinEventOutOfContext)
            ];
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var hook in _hooks)
            {
                if (hook != nint.Zero)
                {
                    _ = UnhookWinEvent(hook);
                }
            }
        }

        private void OnWinEvent(
            nint hook,
            uint eventType,
            nint windowHandle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (_disposed)
            {
                return;
            }

            if (eventType == EventObjectLocationChange
                && (objectId != ObjectIdWindow
                    || (windowHandle != GetForegroundWindow()
                        && windowHandle != FindWindow("Shell_TrayWnd", null))))
            {
                return;
            }

            if (eventType is >= EventObjectCreate and <= EventObjectHide
                && objectId != ObjectIdWindow)
            {
                return;
            }

            _changed();
        }
    }

    private delegate void WinEventCallback(
        nint hook,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitorHandle, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        nint eventHookModule,
        WinEventCallback callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint eventHook);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}

internal readonly record struct TaskbarState(
    nint TaskbarWindowHandle,
    nint MonitorHandle,
    PixelBounds TaskbarBounds,
    PixelBounds MonitorBounds,
    TaskbarEdge Edge,
    uint Dpi,
    bool IsTemporarilyHidden);
