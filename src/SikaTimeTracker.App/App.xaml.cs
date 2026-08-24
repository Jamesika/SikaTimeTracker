using Microsoft.UI.Xaml;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Infrastructure.Data;

namespace SikaTimeTracker;

public partial class App : Application
{
    private Window? _window;
    private IActivityStore? _activityStore;

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
        await _activityStore.InitializeAsync();
        await _activityStore.RecoverOpenActivitiesAsync();

        _window = new MainWindow();
        _window.Activate();
    }
}
