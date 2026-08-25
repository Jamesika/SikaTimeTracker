using Forms = System.Windows.Forms;

namespace SikaTimeTracker.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly System.Drawing.Icon _appIcon;
    private bool _disposed;

    public TrayIconService(MainWindow window)
    {
        _window = window;
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("打开 Sika Time Tracker");
        var exitItem = new Forms.ToolStripMenuItem("退出");
        menu.Items.Add(openItem);
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
        exitItem.Click += (_, _) => Enqueue(async () =>
        {
            Dispose();
            await _window.ExitAsync();
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
        _disposed = true;
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
