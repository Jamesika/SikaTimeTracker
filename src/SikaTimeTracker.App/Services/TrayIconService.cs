using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;
using Forms = System.Windows.Forms;

namespace SikaTimeTracker.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly MainWindow _window;
    private readonly ActivityTrackingService _trackingService;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly System.Drawing.Icon _appIcon;
    private bool _disposed;

    public TrayIconService(MainWindow window, ActivityTrackingService trackingService)
    {
        _window = window;
        _trackingService = trackingService;
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("打开 Sika Time Tracker");
        _pauseItem = new Forms.ToolStripMenuItem("暂停追踪");
        var exitItem = new Forms.ToolStripMenuItem("退出");
        menu.Items.Add(openItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
            ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Sika Time Tracker",
            Icon = _appIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => Enqueue(_window.ShowFromTray);
        openItem.Click += (_, _) => Enqueue(_window.ShowFromTray);
        _pauseItem.Click += (_, _) => Enqueue(async () =>
            await _trackingService.SetPausedAsync(!_trackingService.Status.IsPaused));
        exitItem.Click += (_, _) => Enqueue(async () =>
        {
            Dispose();
            await _window.ExitAsync();
        });
        _trackingService.StatusChanged += OnStatusChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _trackingService.StatusChanged -= OnStatusChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
        _disposed = true;
    }

    private void OnStatusChanged(object? sender, TrackingStatus status)
    {
        Enqueue(() => _pauseItem.Text = status.IsPaused ? "继续追踪" : "暂停追踪");
    }

    private void Enqueue(Action action)
    {
        _window.DispatcherQueue.TryEnqueue(() => action());
    }

    private void Enqueue(Func<Task> action)
    {
        _window.DispatcherQueue.TryEnqueue(async () => await action());
    }
}
