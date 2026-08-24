using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;
using SikaTimeTracker.Views;
using Windows.Graphics;

namespace SikaTimeTracker;

public sealed partial class MainWindow : Window
{
    private readonly ActivityTrackingService _trackingService;
    private bool _allowClose;

    public MainWindow(ActivityTrackingService trackingService)
    {
        _trackingService = trackingService;
        InitializeComponent();
        Title = "Sika Time Tracker";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1180, 760));
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
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

    private async void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        _trackingService.StatusChanged -= OnTrackingStatusChanged;
        await _trackingService.DisposeAsync();
        _allowClose = true;
        Application.Current.Exit();
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
            "rules" => new RulesView(),
            "settings" => new SettingsView(),
            _ => new ActivityView()
        });
    }
}
