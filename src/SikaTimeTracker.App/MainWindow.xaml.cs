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
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ApplyTheme(_preferences.Theme);
        ShowPage("activity");
        _trackingService.StatusChanged += OnTrackingStatusChanged;
        AppWindow.Closing += OnWindowClosing;
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

    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    public void HideToTray()
    {
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
        await _trackingService.DisposeAsync();
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

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag)
        {
            ShowPage(tag);
        }
    }

    private void ShowPage(string tag)
    {
        ContentHost.Children.Clear();
        ContentHost.Children.Add(tag switch
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
        });
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
