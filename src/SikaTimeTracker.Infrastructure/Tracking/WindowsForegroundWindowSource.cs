using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Infrastructure.Tracking;

[SupportedOSPlatform("windows")]
public sealed class WindowsForegroundWindowSource : IForegroundWindowSource
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const int GwlStyle = -16;
    private const int GwlExstyle = -20;
    private const long WsDisabled = 0x08000000L;
    private const long WsChild = 0x40000000L;
    private const long WsExToolwindow = 0x00000080L;
    private const long WsExAppwindow = 0x00040000L;
    private const long WsExLayered = 0x00080000L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoactivate = 0x08000000L;
    private const uint GaRoot = 2;
    private const uint GaRootowner = 3;
    private const uint DwmwaCloaked = 14;
    private static readonly TimeSpan ResolutionCacheDuration = TimeSpan.FromSeconds(10);

    private readonly WinEventDelegate _callback;
    private readonly IWebsiteDomainResolver? _websiteDomainResolver;
    private nint _hook;
    private nint _lastMeaningfulWindowHandle;
    private nint _cachedReportedWindowHandle;
    private nint _cachedResolvedWindowHandle;
    private DateTimeOffset _resolutionCachedAtUtc;
    private bool _disposed;

    public WindowsForegroundWindowSource(IWebsiteDomainResolver? websiteDomainResolver = null)
    {
        _callback = OnWinEvent;
        _websiteDomainResolver = websiteDomainResolver;
    }

    public event EventHandler<WindowChangedEventArgs>? ForegroundWindowChanged;

    public bool CaptureWindowTitles { get; set; } = true;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != 0)
        {
            return;
        }

        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            0,
            _callback,
            0,
            0,
            WineventOutofcontext);
        if (_hook == 0)
        {
            throw new InvalidOperationException("无法监听前台窗口变化");
        }
    }

    public void Stop()
    {
        if (_hook == 0)
        {
            return;
        }

        _ = UnhookWinEvent(_hook);
        _hook = 0;
        _lastMeaningfulWindowHandle = 0;
        _cachedReportedWindowHandle = 0;
        _cachedResolvedWindowHandle = 0;
        _resolutionCachedAtUtc = default;
    }

    public WindowSnapshot? GetCurrentWindow()
    {
        var observedAtUtc = DateTimeOffset.UtcNow;
        return CreateSnapshot(
            ResolveMeaningfulWindowHandle(GetForegroundWindow(), observedAtUtc),
            observedAtUtc);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
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
        var observedAtUtc = DateTimeOffset.UtcNow;
        ForegroundWindowChanged?.Invoke(this, new WindowChangedEventArgs(CreateSnapshot(
            ResolveMeaningfulWindowHandle(windowHandle, observedAtUtc),
            observedAtUtc)));
    }

    private nint ResolveMeaningfulWindowHandle(
        nint reportedHandle,
        DateTimeOffset observedAtUtc)
    {
        if (reportedHandle == 0)
        {
            return _lastMeaningfulWindowHandle != 0
                   && IsLikelyUserSurface(_lastMeaningfulWindowHandle)
                ? _lastMeaningfulWindowHandle
                : 0;
        }

        if (reportedHandle == _cachedReportedWindowHandle
            && observedAtUtc - _resolutionCachedAtUtc < ResolutionCacheDuration)
        {
            return _cachedResolvedWindowHandle;
        }

        var rootHandle = GetAncestor(reportedHandle, GaRoot);
        if (rootHandle == 0)
        {
            rootHandle = reportedHandle;
        }

        if (IsLikelyUserSurface(rootHandle))
        {
            return CacheResolution(reportedHandle, rootHandle, observedAtUtc);
        }

        var rootOwnerHandle = GetAncestor(rootHandle, GaRootowner);
        if (rootOwnerHandle != 0
            && rootOwnerHandle != rootHandle
            && IsLikelyUserSurface(rootOwnerHandle))
        {
            return CacheResolution(reportedHandle, rootOwnerHandle, observedAtUtc);
        }

        if (GetCursorPos(out var cursorPosition))
        {
            var cursorHandle = GetAncestor(WindowFromPoint(cursorPosition), GaRoot);
            if (cursorHandle != 0
                && cursorHandle != rootHandle
                && IsLikelyUserSurface(cursorHandle))
            {
                return CacheResolution(reportedHandle, cursorHandle, observedAtUtc);
            }
        }

        var fallbackHandle = _lastMeaningfulWindowHandle != 0
                             && IsLikelyUserSurface(_lastMeaningfulWindowHandle)
            ? _lastMeaningfulWindowHandle
            : 0;
        return CacheResolution(reportedHandle, fallbackHandle, observedAtUtc);
    }

    private nint CacheResolution(
        nint reportedHandle,
        nint resolvedHandle,
        DateTimeOffset observedAtUtc)
    {
        _cachedReportedWindowHandle = reportedHandle;
        _cachedResolvedWindowHandle = resolvedHandle;
        _resolutionCachedAtUtc = observedAtUtc;
        if (resolvedHandle != 0)
        {
            _lastMeaningfulWindowHandle = resolvedHandle;
        }

        return resolvedHandle;
    }

    private static bool IsLikelyUserSurface(nint windowHandle)
    {
        if (windowHandle == 0 || !GetWindowRect(windowHandle, out var bounds))
        {
            return false;
        }

        var style = GetWindowLongPtr(windowHandle, GwlStyle).ToInt64();
        var extendedStyle = GetWindowLongPtr(windowHandle, GwlExstyle).ToInt64();
        var isCloaked = DwmGetWindowAttribute(
            windowHandle,
            DwmwaCloaked,
            out var cloakState,
            sizeof(int)) == 0
                        && cloakState != 0;
        return ForegroundWindowSurfacePolicy.IsLikelyUserSurface(new ForegroundWindowSurfaceInfo(
            IsWindowVisible(windowHandle),
            IsIconic(windowHandle),
            isCloaked,
            (style & WsChild) != 0,
            (style & WsDisabled) != 0,
            (extendedStyle & WsExNoactivate) != 0,
            (extendedStyle & WsExTransparent) != 0 && (extendedStyle & WsExLayered) != 0,
            (extendedStyle & WsExToolwindow) != 0,
            (extendedStyle & WsExAppwindow) != 0,
            GetWindowTextLength(windowHandle) > 0,
            Math.Max(0, bounds.Right - bounds.Left),
            Math.Max(0, bounds.Bottom - bounds.Top)));
    }

    private WindowSnapshot? CreateSnapshot(nint windowHandle, DateTimeOffset observedAtUtc)
    {
        if (windowHandle == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            return null;
        }

        string processName;
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (!CaptureWindowTitles)
        {
            return new WindowSnapshot(windowHandle, processName, string.Empty, observedAtUtc);
        }

        var titleLength = GetWindowTextLength(windowHandle);
        var titleBuilder = new StringBuilder(Math.Max(titleLength + 1, 1));
        _ = GetWindowText(windowHandle, titleBuilder, titleBuilder.Capacity);
        var websiteDomain = _websiteDomainResolver?.Resolve(windowHandle, processName) ?? string.Empty;
        return new WindowSnapshot(
            windowHandle,
            processName,
            titleBuilder.ToString(),
            observedAtUtc,
            websiteDomain);
    }

    private delegate void WinEventDelegate(
        nint hook,
        uint eventType,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint module,
        WinEventDelegate callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint windowHandle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out Rect bounds);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint windowHandle,
        uint attribute,
        out int value,
        int valueSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
