using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SikaTimeTracker.Views;
using Windows.Graphics;

namespace SikaTimeTracker;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Sika Time Tracker";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1180, 760));
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ShowPage("activity");
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
