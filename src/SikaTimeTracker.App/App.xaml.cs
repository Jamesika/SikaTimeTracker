using Microsoft.UI.Xaml;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Services;
using SikaTimeTracker.Infrastructure.Data;
using SikaTimeTracker.Infrastructure.Tracking;

namespace SikaTimeTracker;

public partial class App : Application
{
    private Window? _window;
    private IActivityStore? _activityStore;
    private ActivityTrackingService? _trackingService;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SikaTimeTracker");
        _activityStore = new SqliteActivityStore(Path.Combine(dataDirectory, "activity.db"));
        _trackingService = new ActivityTrackingService(
            _activityStore,
            new WindowsForegroundWindowSource(),
            new WindowsSystemActivitySource(),
            new ClassificationEngine());

        _window = new MainWindow(_trackingService);
        _window.Activate();
        await _trackingService.StartAsync();
    }
}
