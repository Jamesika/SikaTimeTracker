using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;

namespace SikaTimeTracker.Views;

public sealed partial class ActivityView : UserControl
{
    private readonly IActivityStore _store;
    private readonly ActivityStatisticsService _statistics = new();
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.Local;
    private IReadOnlyList<Category> _categories = [];
    private IReadOnlyList<ActivitySegment> _activities = [];
    private IReadOnlyList<DailyActivityTotal> _dailyTotals = [];
    private DateOnly _selectedDate = DateOnly.FromDateTime(DateTime.Today);
    private int _selectedYear = DateTime.Today.Year;
    private bool _isLoaded;

    public ActivityView(IActivityStore store)
    {
        _store = store;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        await LoadCategoriesAsync();
        await RefreshAsync();
    }

    private async Task LoadCategoriesAsync()
    {
        _categories = await _store.GetCategoriesAsync();
        var filters = new List<CategoryFilterItem> { new(null, "全部分类", "#4F6BED") };
        filters.AddRange(_categories.Select(category => new CategoryFilterItem(
            category.Id,
            category.Name,
            category.Color)));
        CategoryFilter.ItemsSource = filters;
        CategoryFilter.SelectedIndex = 0;
    }

    private async Task RefreshAsync()
    {
        LoadingIndicator.IsActive = true;
        LoadingIndicator.Visibility = Visibility.Visible;
        try
        {
            YearButton.Content = _selectedYear.ToString(CultureInfo.InvariantCulture);
            var firstDate = new DateOnly(_selectedYear, 1, 1);
            var lastDate = new DateOnly(_selectedYear, 12, 31);
            var (rangeStartUtc, _) = ActivityStatisticsService.GetDayBoundsUtc(firstDate, _timeZone);
            var (_, rangeEndUtc) = ActivityStatisticsService.GetDayBoundsUtc(lastDate, _timeZone);
            _activities = await _store.GetActivitiesAsync(rangeStartUtc, rangeEndUtc);
            RenderData();
        }
        finally
        {
            LoadingIndicator.IsActive = false;
            LoadingIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderData()
    {
        var firstDate = new DateOnly(_selectedYear, 1, 1);
        var lastDate = new DateOnly(_selectedYear, 12, 31);
        var categoryId = SelectedCategoryId;
        _dailyTotals = _statistics.BuildDailyTotals(
            _activities,
            firstDate,
            lastDate,
            _timeZone,
            categoryId);
        RenderHeatmap(firstDate, lastDate);
        RenderSummary();
        RenderTimeline();
    }

    private void RenderHeatmap(DateOnly firstDate, DateOnly lastDate)
    {
        HeatmapGrid.Children.Clear();
        HeatmapGrid.ColumnDefinitions.Clear();
        HeatmapGrid.RowDefinitions.Clear();
        MonthHeaderGrid.Children.Clear();
        MonthHeaderGrid.ColumnDefinitions.Clear();

        var firstOffset = ((int)firstDate.DayOfWeek + 6) % 7;
        var gridStart = firstDate.AddDays(-firstOffset);
        var weekCount = ((lastDate.DayNumber - gridStart.DayNumber) / 7) + 1;
        for (var week = 0; week < weekCount; week++)
        {
            HeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
            MonthHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        }

        for (var day = 0; day < 7; day++)
        {
            HeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(13) });
        }

        var totalsByDate = _dailyTotals.ToDictionary(item => item.Date, item => item.Duration);
        var baseColor = ParseColor((CategoryFilter.SelectedItem as CategoryFilterItem)?.Color ?? "#4F6BED");
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            var daysFromStart = date.DayNumber - gridStart.DayNumber;
            var week = daysFromStart / 7;
            var day = daysFromStart % 7;
            var duration = totalsByDate.GetValueOrDefault(date);
            var button = new Button
            {
                Width = 13,
                Height = 13,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                BorderThickness = date == _selectedDate ? new Thickness(2) : new Thickness(0),
                CornerRadius = new CornerRadius(2),
                Background = CreateIntensityBrush(baseColor, duration),
                Tag = date
            };
            if (date == _selectedDate)
            {
                button.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
            }

            ToolTipService.SetToolTip(button, $"{date:yyyy-MM-dd} · {FormatDuration(duration)}");
            button.Click += OnHeatmapDayClicked;
            Grid.SetColumn(button, week);
            Grid.SetRow(button, day);
            HeatmapGrid.Children.Add(button);
        }

        for (var month = 1; month <= 12; month++)
        {
            var monthDate = new DateOnly(_selectedYear, month, 1);
            var week = (monthDate.DayNumber - gridStart.DayNumber) / 7;
            var label = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                Text = $"{month}月"
            };
            Grid.SetColumn(label, Math.Clamp(week, 0, weekCount - 1));
            MonthHeaderGrid.Children.Add(label);
        }
    }

    private void RenderSummary()
    {
        var totalsByDate = _dailyTotals.ToDictionary(item => item.Date, item => item.Duration);
        var selectedTotal = totalsByDate.GetValueOrDefault(_selectedDate);
        var weekdayOffset = ((int)_selectedDate.DayOfWeek + 6) % 7;
        var weekStart = _selectedDate.AddDays(-weekdayOffset);
        var weekTotal = TimeSpan.FromTicks(Enumerable.Range(0, 7)
            .Sum(day => totalsByDate.GetValueOrDefault(weekStart.AddDays(day)).Ticks));
        var activeDays = _dailyTotals.Where(item => item.Duration > TimeSpan.Zero).ToArray();
        var average = activeDays.Length == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)activeDays.Average(item => item.Duration.Ticks));

        TodayTotalText.Text = FormatDuration(selectedTotal);
        WeekTotalText.Text = FormatDuration(weekTotal);
        AverageTotalText.Text = FormatDuration(average);
    }

    private void RenderTimeline()
    {
        var timeline = _statistics.BuildTimeline(
            _activities,
            _selectedDate,
            _timeZone,
            SelectedCategoryId);
        var categoriesById = _categories.ToDictionary(category => category.Id);
        var items = timeline.Select(activity =>
        {
            categoriesById.TryGetValue(activity.CategoryId, out var category);
            var title = string.IsNullOrWhiteSpace(activity.WindowTitle)
                ? activity.ProcessName
                : activity.WindowTitle;
            return new TimelineDisplayItem(
                activity,
                $"{activity.StartLocal:HH:mm}–{activity.EndLocal:HH:mm}",
                title,
                $"{activity.ProcessName} · {category?.Name ?? "未知分类"}",
                FormatDuration(activity.Duration),
                new SolidColorBrush(ParseColor(category?.Color ?? "#8A8886")));
        }).ToArray();

        TimelineTitleText.Text = $"{_selectedDate:yyyy年M月d日} 时间轴";
        TimelineSummaryText.Text = $"共 {items.Length} 段活动，累计 {FormatDuration(timeline.Aggregate(TimeSpan.Zero, (sum, item) => sum + item.Duration))}";
        TimelineList.ItemsSource = items;
        TimelineList.Visibility = items.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTimeline.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnCategoryFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isLoaded && CategoryFilter.SelectedItem is not null)
        {
            RenderData();
        }

        await Task.CompletedTask;
    }

    private async void OnPreviousYearClicked(object sender, RoutedEventArgs args)
    {
        _selectedYear--;
        _selectedDate = new DateOnly(_selectedYear, 1, 1);
        await RefreshAsync();
    }

    private async void OnNextYearClicked(object sender, RoutedEventArgs args)
    {
        _selectedYear++;
        _selectedDate = _selectedYear == DateTime.Today.Year
            ? DateOnly.FromDateTime(DateTime.Today)
            : new DateOnly(_selectedYear, 1, 1);
        await RefreshAsync();
    }

    private async void OnCurrentYearClicked(object sender, RoutedEventArgs args)
    {
        _selectedYear = DateTime.Today.Year;
        _selectedDate = DateOnly.FromDateTime(DateTime.Today);
        await RefreshAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs args)
    {
        await RefreshAsync();
    }

    private void OnTodayClicked(object sender, RoutedEventArgs args)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (_selectedYear != today.Year)
        {
            _selectedYear = today.Year;
            _selectedDate = today;
            _ = RefreshAsync();
            return;
        }

        _selectedDate = today;
        RenderData();
    }

    private void OnHeatmapDayClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: DateOnly date })
        {
            _selectedDate = date;
            RenderData();
        }
    }

    private async void OnTimelineItemClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not TimelineDisplayItem item)
        {
            return;
        }

        var details = new StackPanel { Spacing = 10, MaxWidth = 520 };
        details.Children.Add(new TextBlock { Text = item.TimeText + " · " + item.DurationText });
        details.Children.Add(new TextBlock { Text = item.Activity.ProcessName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        details.Children.Add(new TextBlock { Text = item.Activity.WindowTitle, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "活动详情",
            Content = details,
            CloseButtonText = "关闭"
        };
        await dialog.ShowAsync();
    }

    private long? SelectedCategoryId => (CategoryFilter.SelectedItem as CategoryFilterItem)?.Id;

    private static SolidColorBrush CreateIntensityBrush(Windows.UI.Color baseColor, TimeSpan duration)
    {
        var alpha = duration switch
        {
            { Ticks: <= 0 } => (byte)22,
            { TotalMinutes: < 30 } => (byte)70,
            { TotalHours: < 2 } => (byte)130,
            { TotalHours: < 4 } => (byte)190,
            _ => byte.MaxValue
        };
        var color = duration <= TimeSpan.Zero
            ? Windows.UI.Color.FromArgb(alpha, 128, 128, 128)
            : Windows.UI.Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
        return new SolidColorBrush(color);
    }

    private static Windows.UI.Color ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return Windows.UI.Color.FromArgb(byte.MaxValue, 79, 107, 237);
        }

        return Windows.UI.Color.FromArgb(
            byte.MaxValue,
            (byte)(rgb >> 16),
            (byte)(rgb >> 8),
            (byte)rgb);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return duration <= TimeSpan.Zero ? "0 分钟" : $"{Math.Max(1, (int)duration.TotalSeconds)} 秒";
        }

        var totalHours = (int)duration.TotalHours;
        return totalHours > 0
            ? $"{totalHours} 小时 {duration.Minutes} 分钟"
            : $"{duration.Minutes} 分钟";
    }

    public sealed record CategoryFilterItem(long? Id, string Name, string Color);

    public sealed record TimelineDisplayItem(
        TimelineActivity Activity,
        string TimeText,
        string Title,
        string Subtitle,
        string DurationText,
        Brush CategoryBrush);
}
