using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;

namespace SikaTimeTracker.Views;

public sealed partial class ActivityView : UserControl
{
    private const string SelectedCategorySettingKey = "ActivitySelectedCategoryId";
    private const string AllCategoriesSettingValue = "All";
    private const double MinimumHeatmapCellSize = 13;
    private const double HeatmapCellSpacing = 3;
    private const double MinimumTimelinePixelsPerHour = 48;
    private readonly IActivityStore _store;
    private readonly ActivityTrackingService _trackingService;
    private readonly TimeSpan _minimumActivityDuration;
    private readonly ActivityStatisticsService _statistics = new();
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.Local;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _fastToolTipTimer;
    private IReadOnlyList<Category> _categories = [];
    private IReadOnlyList<ActivitySegment> _activities = [];
    private IReadOnlyList<DailyActivityTotal> _dailyTotals = [];
    private IReadOnlyList<TimelineDisplayItem> _timelineItems = [];
    private DateOnly _selectedDate = DateOnly.FromDateTime(DateTime.Today);
    private int _selectedYear = DateTime.Today.Year;
    private bool _isLoaded;
    private bool _isControlLoaded;
    private bool _isHostActive;
    private bool _isRefreshing;
    private bool _refreshPending;
    private bool _isRestoringCategoryFilter;
    private double _lastHeatmapViewportWidth;
    private double _lastTimelineViewportWidth;
    private FrameworkElement? _pendingFastToolTipTarget;

    public ActivityView(
        IActivityStore store,
        ActivityTrackingService trackingService,
        TimeSpan minimumActivityDuration)
    {
        if (minimumActivityDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumActivityDuration));
        }

        _store = store;
        _trackingService = trackingService;
        _minimumActivityDuration = minimumActivityDuration;
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _fastToolTipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _fastToolTipTimer.Tick += OnFastToolTipTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _isControlLoaded = true;
        UpdateAutoRefreshState();
        if (!_isLoaded)
        {
            _isLoaded = true;
            await LoadCategoriesAsync();
            await RefreshAsync();
            return;
        }

        if (_isHostActive)
        {
            await RefreshAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _isControlLoaded = false;
        HideFastToolTip();
        UpdateAutoRefreshState();
    }

    public void SetHostActive(bool isActive)
    {
        var becameActive = !_isHostActive && isActive;
        _isHostActive = isActive;
        UpdateAutoRefreshState();
        if (becameActive && _isControlLoaded && _isLoaded)
        {
            RequestRefresh();
        }
    }

    public void RequestRefresh()
    {
        if (_isControlLoaded && _isHostActive)
        {
            _ = RefreshAsync();
        }
    }

    private void UpdateAutoRefreshState()
    {
        if (_isControlLoaded && _isHostActive)
        {
            if (!_refreshTimer.IsEnabled)
            {
                _refreshTimer.Start();
            }
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    private void OnRefreshTimerTick(object? sender, object args)
    {
        RequestRefresh();
    }

    private void OnFastToolTipTimerTick(object? sender, object args)
    {
        _fastToolTipTimer.Stop();
        if (_pendingFastToolTipTarget is not null)
        {
            ShowFastToolTip(_pendingFastToolTipTarget);
        }
    }

    private void QueueFastToolTip(FrameworkElement target, string text)
    {
        HideFastToolTip();
        _pendingFastToolTipTarget = target;
        FastToolTipText.Text = text;
        _fastToolTipTimer.Start();
    }

    private void ShowFastToolTip(FrameworkElement target)
    {
        if (!ReferenceEquals(target, _pendingFastToolTipTarget))
        {
            return;
        }

        var origin = target.TransformToVisual(ActivityRoot).TransformPoint(new Windows.Foundation.Point(0, 0));
        FastToolTipOverlay.Visibility = Visibility.Visible;
        FastToolTipOverlay.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = FastToolTipOverlay.DesiredSize;
        var left = Math.Clamp(origin.X, 8, Math.Max(8, ActivityRoot.ActualWidth - size.Width - 8));
        var top = origin.Y + target.ActualHeight + 8;
        if (top + size.Height > ActivityRoot.ActualHeight - 8)
        {
            top = Math.Max(8, origin.Y - size.Height - 8);
        }

        Canvas.SetLeft(FastToolTipOverlay, left);
        Canvas.SetTop(FastToolTipOverlay, top);
    }

    private void HideFastToolTip(FrameworkElement? target = null)
    {
        if (target is not null && !ReferenceEquals(target, _pendingFastToolTipTarget))
        {
            return;
        }

        _fastToolTipTimer.Stop();
        FastToolTipOverlay.Visibility = Visibility.Collapsed;
        _pendingFastToolTipTarget = null;
    }

    private async Task LoadCategoriesAsync()
    {
        _categories = await _store.GetCategoriesAsync();
        var filters = new List<CategoryFilterItem> { new(null, "全部分类", "#2EA043") };
        filters.AddRange(_categories.Select(category => new CategoryFilterItem(
            category.Id,
            category.Name,
            category.Color)));
        var savedValue = await _store.GetSettingAsync(SelectedCategorySettingKey);
        var hasSavedCategory = long.TryParse(
            savedValue,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var savedCategoryId);
        var selectedIndex = hasSavedCategory
            ? filters.FindIndex(filter => filter.Id == savedCategoryId)
            : 0;
        if (selectedIndex < 0)
        {
            selectedIndex = 0;
            await _store.SetSettingAsync(SelectedCategorySettingKey, AllCategoriesSettingValue);
        }

        _isRestoringCategoryFilter = true;
        CategoryFilter.ItemsSource = filters;
        CategoryFilter.SelectedIndex = selectedIndex;
        _isRestoringCategoryFilter = false;
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            _refreshPending = true;
            return;
        }

        _isRefreshing = true;
        LoadingIndicator.IsActive = true;
        LoadingIndicator.Visibility = Visibility.Visible;
        try
        {
            YearButton.Content = _selectedYear.ToString(CultureInfo.InvariantCulture);
            var firstDate = new DateOnly(_selectedYear, 1, 1);
            var lastDate = new DateOnly(_selectedYear, 12, 31);
            var (rangeStartUtc, _) = ActivityStatisticsService.GetDayBoundsUtc(firstDate, _timeZone);
            var (_, rangeEndUtc) = ActivityStatisticsService.GetDayBoundsUtc(lastDate, _timeZone);
            _activities = (await _store.GetActivitiesAsync(rangeStartUtc, rangeEndUtc))
                .Where(activity => ActivityDisplayPolicy.ShouldDisplay(activity, _minimumActivityDuration))
                .ToArray();
            RenderData();
        }
        finally
        {
            LoadingIndicator.IsActive = false;
            LoadingIndicator.Visibility = Visibility.Collapsed;
            _isRefreshing = false;
            if (_refreshPending && _isControlLoaded && _isHostActive)
            {
                _refreshPending = false;
                _ = RefreshAsync();
            }
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
        HideFastToolTip();
        HeatmapGrid.Children.Clear();
        HeatmapGrid.ColumnDefinitions.Clear();
        HeatmapGrid.RowDefinitions.Clear();
        MonthHeaderGrid.Children.Clear();
        MonthHeaderGrid.ColumnDefinitions.Clear();

        var firstOffset = ((int)firstDate.DayOfWeek + 6) % 7;
        var gridStart = firstDate.AddDays(-firstOffset);
        var weekCount = ((lastDate.DayNumber - gridStart.DayNumber) / 7) + 1;
        var viewportWidth = Math.Max(0, HeatmapScrollViewer.ActualWidth - 14);
        var minimumWidth = weekCount * MinimumHeatmapCellSize + (weekCount - 1) * HeatmapCellSpacing;
        var heatmapScale = viewportWidth > minimumWidth ? viewportWidth / minimumWidth : 1;
        var cellSize = MinimumHeatmapCellSize * heatmapScale;
        var cellSpacing = HeatmapCellSpacing * heatmapScale;
        var totalWidth = weekCount * cellSize + (weekCount - 1) * cellSpacing;
        HeatmapGrid.Width = totalWidth;
        HeatmapGrid.ColumnSpacing = cellSpacing;
        HeatmapGrid.RowSpacing = cellSpacing;
        MonthHeaderGrid.Width = totalWidth;
        MonthHeaderGrid.ColumnSpacing = cellSpacing;
        for (var week = 0; week < weekCount; week++)
        {
            HeatmapGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cellSize) });
            MonthHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cellSize) });
        }

        for (var day = 0; day < 7; day++)
        {
            HeatmapGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cellSize) });
        }

        var localToday = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone).DateTime);

        var totalsByDate = _dailyTotals.ToDictionary(item => item.Date, item => item.Duration);
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            var daysFromStart = date.DayNumber - gridStart.DayNumber;
            var week = daysFromStart / 7;
            var day = daysFromStart % 7;
            var duration = totalsByDate.GetValueOrDefault(date);
            var intensityBrush = CreateIntensityBrush(duration);
            var button = new Button
            {
                Width = cellSize,
                Height = cellSize,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                BorderThickness = date == _selectedDate ? new Thickness(2) : new Thickness(0),
                CornerRadius = new CornerRadius(2),
                Background = intensityBrush,
                Opacity = date > localToday ? 0.35 : 1,
                Tag = date
            };
            button.Resources["ButtonBackground"] = intensityBrush;
            button.Resources["ButtonBackgroundPointerOver"] = intensityBrush;
            button.Resources["ButtonBackgroundPressed"] = intensityBrush;
            if (date == _selectedDate)
            {
                button.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
            }

            var dayDescription = $"{date:yyyy-MM-dd} · {FormatDuration(duration)}";
            AutomationProperties.SetName(button, dayDescription);
            button.PointerEntered += (_, _) => QueueFastToolTip(button, dayDescription);
            button.PointerExited += (_, _) => HideFastToolTip(button);
            button.PointerCanceled += (_, _) => HideFastToolTip(button);
            button.Click += OnHeatmapDayClicked;
            Grid.SetColumn(button, week);
            Grid.SetRow(button, day);
            HeatmapGrid.Children.Add(button);
        }

        for (var month = 1; month <= 12; month++)
        {
            var monthDate = new DateOnly(_selectedYear, month, 1);
            var week = (monthDate.DayNumber - gridStart.DayNumber) / 7;
            var nextMonthDate = month == 12
                ? lastDate.AddDays(1)
                : new DateOnly(_selectedYear, month + 1, 1);
            var nextWeek = Math.Min(weekCount, (nextMonthDate.DayNumber - gridStart.DayNumber) / 7);
            var label = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                Text = $"{month}月",
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.None
            };
            Grid.SetColumn(label, Math.Clamp(week, 0, weekCount - 1));
            Grid.SetColumnSpan(label, Math.Max(1, nextWeek - week));
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
        _timelineItems = timeline.Select(activity =>
        {
            categoriesById.TryGetValue(activity.CategoryId, out var category);
            var appName = FormatApplicationName(activity.ProcessName);
            var title = string.IsNullOrWhiteSpace(activity.WindowTitle)
                ? appName
                : activity.WindowTitle;
            return new TimelineDisplayItem(
                activity,
                $"{activity.StartLocal:HH:mm}–{activity.EndLocal:HH:mm}",
                appName,
                title,
                category?.Name ?? "未知分类",
                $"{title} · {category?.Name ?? "未知分类"}",
                FormatDuration(activity.Duration),
                new SolidColorBrush(ParseColor(category?.Color ?? "#8A8886")));
        }).ToArray();

        TimelineTitleText.Text = $"{_selectedDate:yyyy年M月d日} 时间轴";
        TimelineSummaryText.Text = $"共 {_timelineItems.Count} 段活动，累计 {FormatDuration(timeline.Aggregate(TimeSpan.Zero, (sum, item) => sum + item.Duration))}";
        TimelineViewport.Visibility = _timelineItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTimeline.Visibility = _timelineItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivityDetailsHeader.Visibility = _timelineItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TimelineList.Visibility = _timelineItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TimelineList.ItemsSource = BuildSoftwareUsageItems(_timelineItems);
        RenderTimelineCanvas();
    }

    private static IReadOnlyList<SoftwareUsageItem> BuildSoftwareUsageItems(
        IReadOnlyList<TimelineDisplayItem> timelineItems)
    {
        var groups = timelineItems
            .GroupBy(
                item => string.IsNullOrWhiteSpace(item.Activity.WebsiteDomain)
                    ? $"app:{item.Activity.ProcessName}"
                    : $"web:{item.Activity.WebsiteDomain}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToArray();
                var duration = TimeSpan.FromTicks(items.Sum(item => item.Activity.Duration.Ticks));
                var dominantCategory = items
                    .GroupBy(item => item.CategoryName, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(category => category.Sum(item => item.Activity.Duration.Ticks))
                    .First();
                var representative = dominantCategory.First();
                var domain = items
                    .Select(item => item.Activity.WebsiteDomain)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                var applications = string.Join(
                    " / ",
                    items.Select(item => item.ApplicationName).Distinct(StringComparer.OrdinalIgnoreCase));
                return new
                {
                    DisplayName = domain ?? representative.ApplicationName,
                    Subtitle = domain is null
                        ? $"{representative.CategoryName} · {items.Length} 段活动"
                        : $"{applications} · {representative.CategoryName} · {items.Length} 段活动",
                    Duration = duration,
                    representative.CategoryBrush
                };
            })
            .OrderByDescending(item => item.Duration)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var maximumTicks = Math.Max(1, groups.FirstOrDefault()?.Duration.Ticks ?? 1);
        return groups.Select(item =>
        {
            var percentage = Math.Clamp(item.Duration.Ticks * 100d / maximumTicks, 2, 100);
            return new SoftwareUsageItem(
                item.DisplayName,
                item.Subtitle,
                FormatDuration(item.Duration),
                item.CategoryBrush,
                new GridLength(percentage, GridUnitType.Star),
                new GridLength(100 - percentage, GridUnitType.Star));
        }).ToArray();
    }

    private void RenderTimelineCanvas()
    {
        HideFastToolTip();
        TimelineCanvas.Children.Clear();
        if (_timelineItems.Count == 0)
        {
            return;
        }

        var visibleStartMinutes = Math.Floor(
            _timelineItems.Min(item => GetMinuteOfDay(item.Activity.StartLocal, isEnd: false)) / 60d) * 60d;
        var visibleEndMinutes = Math.Ceiling(
            _timelineItems.Max(item => GetMinuteOfDay(item.Activity.EndLocal, isEnd: true)) / 60d) * 60d;
        visibleStartMinutes = Math.Clamp(visibleStartMinutes, 0, 1380);
        visibleEndMinutes = Math.Clamp(visibleEndMinutes, visibleStartMinutes + 60, 1440);
        var visibleMinutes = visibleEndMinutes - visibleStartMinutes;
        var width = Math.Max(
            TimelineViewport.ActualWidth - 2,
            visibleMinutes / 60d * MinimumTimelinePixelsPerHour);
        TimelineCanvas.Width = width;
        var trackTop = 38d;
        var laneHeight = 44d;
        var segmentHeight = 36d;
        var laneAssignments = _statistics.AssignTimelineLanes(_timelineItems.Select(item => item.Activity));
        var laneCount = laneAssignments.Count == 0 ? 1 : laneAssignments.Values.Max() + 1;
        var canvasHeight = trackTop + laneCount * laneHeight + 12;
        TimelineCanvas.Height = canvasHeight;
        var gridBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(48, 128, 128, 128));
        var labelBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(210, 128, 128, 128));
        for (var lane = 0; lane < laneCount; lane++)
        {
            var track = new Border
            {
                Width = width,
                Height = laneHeight - 4,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                    lane % 2 == 0 ? (byte)24 : (byte)14,
                    128,
                    128,
                    128)),
                CornerRadius = new CornerRadius(5)
            };
            Canvas.SetTop(track, trackTop + lane * laneHeight);
            TimelineCanvas.Children.Add(track);
        }

        var startHour = (int)(visibleStartMinutes / 60d);
        var endHour = (int)(visibleEndMinutes / 60d);
        var tickIntervalHours = endHour - startHour <= 8 ? 1 : 2;
        var tickHours = Enumerable.Range(startHour, endHour - startHour + 1)
            .Where(hour => hour == startHour
                           || hour == endHour
                           || (hour - startHour) % tickIntervalHours == 0)
            .Distinct()
            .OrderBy(hour => hour);
        foreach (var hour in tickHours)
        {
            var x = width * (hour * 60d - visibleStartMinutes) / visibleMinutes;
            var line = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = 26,
                Y2 = canvasHeight - 4,
                Stroke = gridBrush,
                StrokeThickness = hour == startHour || hour == endHour ? 1.5 : 1
            };
            TimelineCanvas.Children.Add(line);
            var label = new TextBlock
            {
                Width = 44,
                Text = $"{hour:00}:00",
                FontSize = 11,
                Foreground = labelBrush,
                TextAlignment = TextAlignment.Center
            };
            Canvas.SetLeft(label, Math.Clamp(x - 22, 0, width - 44));
            Canvas.SetTop(label, 4);
            TimelineCanvas.Children.Add(label);
        }

        foreach (var item in _timelineItems.OrderBy(item => item.Activity.StartLocal))
        {
            var startMinutes = GetMinuteOfDay(item.Activity.StartLocal, isEnd: false);
            var endMinutes = GetMinuteOfDay(item.Activity.EndLocal, isEnd: true);
            var left = width * (startMinutes - visibleStartMinutes) / visibleMinutes;
            var segmentWidth = Math.Max(6, width * Math.Max(0, endMinutes - startMinutes) / visibleMinutes);
            segmentWidth = Math.Min(segmentWidth, width - left);
            var label = new TextBlock
            {
                Text = segmentWidth >= 52 ? item.ApplicationName : string.Empty,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var segment = new Border
            {
                Width = segmentWidth,
                Height = segmentHeight,
                Padding = segmentWidth >= 52 ? new Thickness(7, 0, 7, 0) : new Thickness(0),
                Background = item.CategoryBrush,
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Child = label,
                Tag = item
            };
            var segmentDescription = $"{item.ApplicationName}\n{item.Title}\n分类：{item.CategoryName}\n时间：{item.TimeText}\n持续：{item.DurationText}";
            AutomationProperties.SetName(segment, segmentDescription);
            segment.PointerEntered += (_, _) => QueueFastToolTip(segment, segmentDescription);
            segment.PointerExited += (_, _) => HideFastToolTip(segment);
            segment.PointerCanceled += (_, _) => HideFastToolTip(segment);
            segment.Tapped += OnTimelineSegmentTapped;
            Canvas.SetLeft(segment, left);
            Canvas.SetTop(segment, trackTop + laneAssignments[item.Activity.ActivityId] * laneHeight + 2);
            TimelineCanvas.Children.Add(segment);
        }
    }

    private double GetMinuteOfDay(DateTimeOffset value, bool isEnd)
    {
        var valueDate = DateOnly.FromDateTime(value.Date);
        if (valueDate < _selectedDate)
        {
            return 0;
        }

        if (valueDate > _selectedDate)
        {
            return 1440;
        }

        if (isEnd && value.TimeOfDay == TimeSpan.Zero && valueDate == _selectedDate.AddDays(1))
        {
            return 1440;
        }

        return value.TimeOfDay.TotalMinutes;
    }

    private void OnHeatmapViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!_isLoaded || Math.Abs(args.NewSize.Width - _lastHeatmapViewportWidth) < 1)
        {
            return;
        }

        _lastHeatmapViewportWidth = args.NewSize.Width;
        RenderHeatmap(new DateOnly(_selectedYear, 1, 1), new DateOnly(_selectedYear, 12, 31));
    }

    private void OnTimelineViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!_isLoaded || Math.Abs(args.NewSize.Width - _lastTimelineViewportWidth) < 1)
        {
            return;
        }

        _lastTimelineViewportWidth = args.NewSize.Width;
        RenderTimelineCanvas();
    }

    private async void OnCategoryFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isRestoringCategoryFilter || CategoryFilter.SelectedItem is not CategoryFilterItem selectedFilter)
        {
            return;
        }

        if (_isLoaded && CategoryFilter.SelectedItem is not null)
        {
            RenderData();
        }

        var settingValue = selectedFilter.Id?.ToString(CultureInfo.InvariantCulture)
            ?? AllCategoriesSettingValue;
        await _store.SetSettingAsync(SelectedCategorySettingKey, settingValue);
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

    private void OnTimelineSegmentTapped(object sender, TappedRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: TimelineDisplayItem item })
        {
            return;
        }

        _ = ShowTimelineDetailsAsync(item);
    }

    private async Task ShowTimelineDetailsAsync(TimelineDisplayItem item)
    {
        var details = new StackPanel { Spacing = 10, MaxWidth = 520 };
        details.Children.Add(new TextBlock { Text = item.TimeText + " · " + item.DurationText });
        details.Children.Add(new TextBlock { Text = item.Activity.ProcessName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(item.Activity.WebsiteDomain))
        {
            details.Children.Add(new TextBlock { Text = item.Activity.WebsiteDomain });
        }
        details.Children.Add(new TextBlock { Text = item.Activity.WindowTitle, TextWrapping = TextWrapping.Wrap });
        var isWebsite = !string.IsNullOrWhiteSpace(item.Activity.WebsiteDomain);
        details.Children.Add(new TextBlock
        {
            Text = isWebsite
                ? "修改后会更新这个网站域名的全部历史记录，并为后续访问建立自动分类规则。"
                : "修改后会更新这个程序的全部历史记录，并为后续活动建立自动分类规则。",
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap
        });
        var categoryBox = new ComboBox
        {
            Header = "手动分类",
            ItemsSource = _categories,
            DisplayMemberPath = nameof(Category.Name),
            SelectedItem = _categories.FirstOrDefault(category => category.Id == item.Activity.CategoryId),
            MinWidth = 260
        };
        details.Children.Add(categoryBox);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "活动详情",
            Content = details,
            PrimaryButtonText = isWebsite ? "应用到该网站全部记录" : "应用到该程序全部记录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && categoryBox.SelectedItem is Category category)
        {
            var classificationService = new ProgramClassificationService(_store);
            var changed = isWebsite
                ? await classificationService.AssignWebsiteDomainCategoryAsync(
                    item.Activity.WebsiteDomain,
                    category.Id)
                : await classificationService.AssignCategoryAsync(
                    item.Activity.ProcessName,
                    category.Id);
            await _trackingService.ReloadRulesAsync();
            await RefreshAsync();
            TimelineSummaryText.Text += $" · 已更新该程序 {changed} 条记录";
        }
    }

    private long? SelectedCategoryId => (CategoryFilter.SelectedItem as CategoryFilterItem)?.Id;

    private static SolidColorBrush CreateIntensityBrush(TimeSpan duration)
    {
        var color = duration switch
        {
            { TotalMinutes: <= 20 } => Windows.UI.Color.FromArgb(26, 140, 149, 159),
            { TotalHours: < 2 } => Windows.UI.Color.FromArgb(255, 155, 233, 168),
            { TotalHours: < 4 } => Windows.UI.Color.FromArgb(255, 64, 196, 99),
            { TotalHours: < 6 } => Windows.UI.Color.FromArgb(255, 48, 161, 78),
            { TotalHours: < 8 } => Windows.UI.Color.FromArgb(255, 40, 136, 68),
            _ => Windows.UI.Color.FromArgb(255, 33, 110, 57)
        };
        return new SolidColorBrush(color);
    }

    private static string FormatApplicationName(string processName)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(processName);
        return string.IsNullOrWhiteSpace(name) ? processName : name;
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
        string ApplicationName,
        string Title,
        string CategoryName,
        string Subtitle,
        string DurationText,
        Brush CategoryBrush);

    public sealed record SoftwareUsageItem(
        string DisplayName,
        string Subtitle,
        string DurationText,
        Brush CategoryBrush,
        GridLength FilledWidth,
        GridLength RemainingWidth);
}
