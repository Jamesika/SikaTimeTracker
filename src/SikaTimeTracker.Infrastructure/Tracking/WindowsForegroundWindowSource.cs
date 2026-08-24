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

    private readonly WinEventDelegate _callback;
    private nint _hook;
    private bool _disposed;

    public WindowsForegroundWindowSource()
    {
        _callback = OnWinEvent;
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
    }

    public WindowSnapshot? GetCurrentWindow()
    {
        return CreateSnapshot(GetForegroundWindow(), DateTimeOffset.UtcNow);
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
        ForegroundWindowChanged?.Invoke(
            this,
            new WindowChangedEventArgs(CreateSnapshot(windowHandle, DateTimeOffset.UtcNow)));
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
        return new WindowSnapshot(windowHandle, processName, titleBuilder.ToString(), observedAtUtc);
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
}
