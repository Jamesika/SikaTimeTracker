using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;
using SikaTimeTracker.Infrastructure.Data;

namespace SikaTimeTracker.Views;

public sealed partial class SettingsView : UserControl
{
    private readonly IActivityStore _store;
    private readonly ApplicationSettingsService _settingsService;
    private readonly IStartupService _startupService;
    private readonly ActivityTrackingService _trackingService;
    private readonly Action<AppPreferences> _applyPreferences;
    private readonly string _dataDirectory;
    private AppPreferences _preferences;

    public SettingsView(
        IActivityStore store,
        ApplicationSettingsService settingsService,
        IStartupService startupService,
        ActivityTrackingService trackingService,
        AppPreferences preferences,
        string dataDirectory,
        Action<AppPreferences> applyPreferences)
    {
        _store = store;
        _settingsService = settingsService;
        _startupService = startupService;
        _trackingService = trackingService;
        _preferences = preferences;
        _dataDirectory = dataDirectory;
        _applyPreferences = applyPreferences;
        InitializeComponent();
        PopulateControls();
    }

    private void PopulateControls()
    {
        RunAtStartupToggle.IsOn = _startupService.IsEnabled();
        StartMinimizedToggle.IsOn = _preferences.StartMinimized;
        IdleDetectionToggle.IsOn = _preferences.IdleDetectionEnabled;
        IdleMinutesBox.Value = _preferences.IdleThresholdMinutes;
        MinimumSecondsBox.Value = _preferences.MinimumActivitySeconds;
        MergeGapSecondsBox.Value = _preferences.MergeGapSeconds;
        RecordTitlesToggle.IsOn = _preferences.RecordWindowTitles;
        var themes = new[]
        {
            new ThemeChoice(AppTheme.System, "跟随系统"),
            new ThemeChoice(AppTheme.Light, "浅色"),
            new ThemeChoice(AppTheme.Dark, "深色")
        };
        ThemeBox.ItemsSource = themes;
        ThemeBox.SelectedItem = themes.First(choice => choice.Value == _preferences.Theme);
        DatabasePathBox.Text = Path.Combine(_dataDirectory, "activity.db");
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs args)
    {
        SetBusy(true);
        try
        {
            _preferences = new AppPreferences
            {
                RunAtStartup = RunAtStartupToggle.IsOn,
                StartMinimized = StartMinimizedToggle.IsOn,
                IdleDetectionEnabled = IdleDetectionToggle.IsOn,
                IdleThresholdMinutes = GetNumber(IdleMinutesBox, 5),
                MinimumActivitySeconds = GetNumber(MinimumSecondsBox, 2),
                MergeGapSeconds = GetNumber(MergeGapSecondsBox, 10),
                RecordWindowTitles = RecordTitlesToggle.IsOn,
                Theme = ((ThemeChoice)ThemeBox.SelectedItem).Value
            };
            await _settingsService.SaveAsync(_preferences);
            _startupService.SetEnabled(_preferences.RunAtStartup, _preferences.StartMinimized);
            await _trackingService.UpdateConfigurationAsync(
                new ActivityTrackingOptions
                {
                    IdleDetectionEnabled = _preferences.IdleDetectionEnabled,
                    IdleThreshold = TimeSpan.FromMinutes(_preferences.IdleThresholdMinutes),
                    MinimumActivityDuration = TimeSpan.FromSeconds(_preferences.MinimumActivitySeconds),
                    AdjacentMergeGap = TimeSpan.FromSeconds(_preferences.MergeGapSeconds)
                },
                _preferences.RecordWindowTitles);
            _applyPreferences(_preferences);
            ShowMessage("设置已保存并生效", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowMessage($"保存失败：{exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnOpenDataFolderClicked(object sender, RoutedEventArgs args)
    {
        Directory.CreateDirectory(_dataDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dataDirectory}\"")
        {
            UseShellExecute = true
        });
    }

    private async void OnExportClicked(object sender, RoutedEventArgs args)
    {
        SetBusy(true);
        try
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var destination = Path.Combine(documents, "SikaTimeTracker");
            var exporter = new ActivityCsvExporter(_store);
            var path = await exporter.ExportAsync(destination);
            ShowMessage($"已导出到 {path}", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowMessage($"导出失败：{exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnClearDataClicked(object sender, RoutedEventArgs args)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空全部活动数据",
            Content = "此操作无法撤销。分类、规则和设置会保留。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        var wasPaused = _trackingService.Status.IsPaused;
        try
        {
            await _trackingService.SetPausedAsync(true);
            var deleted = await _store.DeleteAllActivitiesAsync();
            ShowMessage($"已清空 {deleted} 条活动记录", InfoBarSeverity.Success);
        }
        finally
        {
            if (!wasPaused)
            {
                await _trackingService.SetPausedAsync(false);
            }

            SetBusy(false);
        }
    }

    private static int GetNumber(NumberBox box, int fallback)
    {
        return double.IsNaN(box.Value) ? fallback : checked((int)box.Value);
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        PageMessage.Message = message;
        PageMessage.Severity = severity;
        PageMessage.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        BusyIndicator.IsActive = busy;
        BusyIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    public sealed record ThemeChoice(AppTheme Value, string Name);
}
