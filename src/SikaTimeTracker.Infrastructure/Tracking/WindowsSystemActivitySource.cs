using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Infrastructure.Tracking;

[SupportedOSPlatform("windows")]
public sealed class WindowsSystemActivitySource : ISystemActivitySource
{
    private bool _isSessionConnected = true;
    private bool _isPowerActive = true;
    private bool _isStarted;
    private bool _disposed;

    public event EventHandler<SystemActivityChangedEventArgs>? SystemActivityChanged;

    public bool IsSessionInteractive => _isSessionConnected && _isPowerActive;

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
                IsSessionInteractive,
                DateTimeOffset.UtcNow,
                reason));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);
}
