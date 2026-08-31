using Forms = System.Windows.Forms;

namespace SikaTimeTracker.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly MainWindow _window;
    private readonly Func<bool> _isDarkThemeProvider;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly System.Drawing.Icon _appIcon;
    private ModernTrayMenuForm? _activeMenu;
    private bool _disposed;

    public TrayIconService(MainWindow window, Func<bool> isDarkThemeProvider)
    {
        _window = window;
        _isDarkThemeProvider = isDarkThemeProvider;

        _appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
            ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Sika Time Tracker",
            Icon = _appIcon,
            Visible = true
        };
        _notifyIcon.MouseUp += OnMouseUp;
        _notifyIcon.DoubleClick += (_, _) => Enqueue(_window.ShowFromTray);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _activeMenu?.Close();
        _activeMenu = null;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
        _disposed = true;
    }

    private void OnMouseUp(object? sender, Forms.MouseEventArgs args)
    {
        if (args.Button == Forms.MouseButtons.Right)
        {
            ShowMenu();
        }
    }

    private void ShowMenu()
    {
        _activeMenu?.Close();
        _activeMenu = null;
        var menu = new ModernTrayMenuForm(
            !_isDarkThemeProvider(),
            new[]
            {
                new TrayMenuEntry("打开 Sika Time Tracker", () => Enqueue(_window.ShowFromTray)),
                new TrayMenuEntry("退出", () => Enqueue(async () =>
                {
                    Dispose();
                    await _window.ExitAsync();
                }))
            });
        _activeMenu = menu;
        menu.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_activeMenu, menu))
            {
                _activeMenu = null;
            }
        };
        menu.ShowAt(Forms.Cursor.Position);
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
