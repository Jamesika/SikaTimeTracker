using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Infrastructure.Tracking;

[SupportedOSPlatform("windows")]
public sealed class WindowsSystemActivitySource : ISystemActivitySource
{
    private const uint WtsCurrentSession = uint.MaxValue;
    private const int WtsSessionInfoEx = 25;
    private const uint WtsInfoExLevelNumber = 1;
    private const int WtsSessionLocked = 0;
    private const int WtsSessionUnlocked = 1;

    private bool _isSessionConnected = true;
    private bool _isPowerActive = true;
    private bool _isStarted;
    private bool _disposed;

    public event EventHandler<SystemActivityChangedEventArgs>? SystemActivityChanged;

    public bool IsSessionInteractive
    {
        get
        {
            RefreshSessionConnectionState();
            return CurrentIsSessionInteractive;
        }
    }

    private bool CurrentIsSessionInteractive => _isSessionConnected && _isPowerActive;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isStarted)
        {
            return;
        }

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        _isStarted = true;
    }

    public void Stop()
    {
        if (!_isStarted)
        {
            return;
        }

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _isStarted = false;
    }

    public TimeSpan GetIdleDuration()
    {
        var inputInfo = new LastInputInfo
        {
            Size = checked((uint)Marshal.SizeOf<LastInputInfo>())
        };
        if (!GetLastInputInfo(ref inputInfo))
        {
            return TimeSpan.Zero;
        }

        var elapsedMilliseconds = unchecked((uint)Environment.TickCount - inputInfo.Time);
        return TimeSpan.FromMilliseconds(elapsedMilliseconds);
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

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args)
    {
        switch (args.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.SessionLogoff:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _isSessionConnected = false;
                RaiseChanged(args.Reason.ToString());
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogon:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _isSessionConnected = true;
                RaiseChanged(args.Reason.ToString());
                break;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs args)
    {
        switch (args.Mode)
        {
            case PowerModes.Suspend:
                _isPowerActive = false;
                RaiseChanged("Suspend");
                break;
            case PowerModes.Resume:
                _isPowerActive = true;
                RaiseChanged("Resume");
                break;
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs args)
    {
        _isSessionConnected = false;
        RaiseChanged(args.Reason.ToString());
    }

    private void RaiseChanged(string reason)
    {
        SystemActivityChanged?.Invoke(
            this,
            new SystemActivityChangedEventArgs(
                CurrentIsSessionInteractive,
                DateTimeOffset.UtcNow,
                reason));
    }

    private void RefreshSessionConnectionState()
    {
        nint buffer = 0;
        try
        {
            if (!WTSQuerySessionInformation(
                    0,
                    WtsCurrentSession,
                    WtsSessionInfoEx,
                    out buffer,
                    out var returnedBytes)
                || buffer == 0
                || returnedBytes < Marshal.SizeOf<WtsInfoEx>())
            {
                return;
            }

            var sessionInfo = Marshal.PtrToStructure<WtsInfoEx>(buffer);
            if (sessionInfo.Level != WtsInfoExLevelNumber)
            {
                return;
            }

            var sessionFlags = sessionInfo.Data.Level1.SessionFlags;
            if (sessionFlags is WtsSessionLocked or WtsSessionUnlocked)
            {
                _isSessionConnected = sessionFlags == WtsSessionUnlocked;
            }
        }
        finally
        {
            if (buffer != 0)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsInfoEx
    {
        public uint Level;
        public WtsInfoExLevel Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct WtsInfoExLevel
    {
        [FieldOffset(0)]
        public long Alignment;

        [FieldOffset(0)]
        public WtsInfoExLevel1Prefix Level1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsInfoExLevel1Prefix
    {
        public uint SessionId;
        public int SessionState;
        public int SessionFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        nint serverHandle,
        uint sessionId,
        int infoClass,
        out nint buffer,
        out uint returnedBytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint memory);
}
