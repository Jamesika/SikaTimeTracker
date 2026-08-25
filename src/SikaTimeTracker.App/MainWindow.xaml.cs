using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;
using SikaTimeTracker.Views;
using Windows.Graphics;

namespace SikaTimeTracker;

public sealed partial class MainWindow : Window
{
    private readonly ActivityTrackingService _trackingService;
    private readonly IActivityStore _activityStore;
    private readonly ApplicationSettingsService _settingsService;
    private readonly IStartupService _startupService;
    private readonly string _dataDirectory;
    private AppPreferences _preferences;
    private bool _exitRequested;
    private bool _isChangingSelection;
    private bool _isWindowActive = true;
    private string _currentTag = "activity";
    private FrameworkElement? _currentPage;

    public event EventHandler? Exiting;

    public MainWindow(
        ActivityTrackingService trackingService,
        IActivityStore activityStore,
        ApplicationSettingsService settingsService,
        IStartupService startupService,
        AppPreferences preferences,
        string dataDirectory)
    {
        _trackingService = trackingService;
        _activityStore = activityStore;
        _settingsService = settingsService;
        _startupService = startupService;
        _preferences = preferences;
        _dataDirectory = dataDirectory;
        InitializeComponent();
        Title = "Sika Time Tracker";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1180, 760));
        _isChangingSelection = true;
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        _isChangingSelection = false;
        ApplyTheme(_preferences.Theme);
        ShowPage("activity");
        _trackingService.StatusChanged += OnTrackingStatusChanged;
        _trackingService.ActivityRecorded += OnActivityRecorded;
        AppWindow.Closing += OnWindowClosing;
        Activated += OnWindowActivated;
    }

    private async void OnPauseClicked(object sender, RoutedEventArgs args)
    {
        await _trackingService.SetPausedAsync(!_trackingService.Status.IsPaused);
    }

    private void OnTrackingStatusChanged(object? sender, TrackingStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = status.StatusText;
            PauseButton.Content = status.IsPaused ? "继续追踪" : "暂停追踪";
            StatusIndicator.Opacity = status.IsTracking ? 1 : 0.45;
        });
    }

    private void OnActivityRecorded(object? sender, EventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_currentPage is ActivityView activity)
            {
                activity.RequestRefresh();
            }
        });
    }

    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
        SetWindowActive(true);
    }

    public void HideToTray()
    {
        SetWindowActive(false);
        AppWindow.Hide();
    }

    public void ApplyPreferences(AppPreferences preferences)
    {
        _preferences = preferences;
        ApplyTheme(preferences.Theme);
    }

    public async Task ExitAsync()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        _trackingService.StatusChanged -= OnTrackingStatusChanged;
        _trackingService.ActivityRecorded -= OnActivityRecorded;
        await _trackingService.DisposeAsync();
        Exiting?.Invoke(this, EventArgs.Empty);
        Application.Current.Exit();
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_exitRequested)
        {
            return;
        }

        args.Cancel = true;
        HideToTray();
    }

    private async void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isChangingSelection
            || args.SelectedItemContainer?.Tag is not string tag
            || tag == _currentTag)
        {
            return;
        }

        if (_currentPage is SettingsView settings && !await settings.ConfirmNavigationAsync())
        {
            _isChangingSelection = true;
            RootNavigation.SelectedItem = RootNavigation.MenuItems
                .OfType<NavigationViewItem>()
                .First(item => string.Equals(item.Tag as string, _currentTag, StringComparison.Ordinal));
            _isChangingSelection = false;
            return;
        }

        ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        if (_currentPage is ActivityView previousActivity)
        {
            previousActivity.SetHostActive(false);
        }

        ContentHost.Children.Clear();
        _currentPage = tag switch
        {
            "rules" => new RulesView(_activityStore, _trackingService),
            "settings" => new SettingsView(
                _activityStore,
                _settingsService,
                _startupService,
                _trackingService,
                _preferences,
                _dataDirectory,
                ApplyPreferences),
            _ => new ActivityView(_activityStore)
        };
        _currentTag = tag;
        ContentHost.Children.Add(_currentPage);
        if (_currentPage is ActivityView activity)
        {
            activity.SetHostActive(_isWindowActive);
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        SetWindowActive(args.WindowActivationState != WindowActivationState.Deactivated);
    }

    private void SetWindowActive(bool isActive)
    {
        _isWindowActive = isActive;
        if (_currentPage is ActivityView activity)
        {
            activity.SetHostActive(isActive);
        }
    }

    private void ApplyTheme(AppTheme theme)
    {
        RootNavigation.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }
}
