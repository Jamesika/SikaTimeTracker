using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;

namespace SikaTimeTracker.Views;

public sealed partial class RulesView : UserControl
{
    private readonly IActivityStore _store;
    private readonly ActivityTrackingService _trackingService;
    private readonly ClassificationEngine _classificationEngine = new();
    private IReadOnlyList<Category> _categories = [];
    private IReadOnlyList<ClassificationRule> _rules = [];
    private bool _isLoaded;

    public RulesView(IActivityStore store, ActivityTrackingService trackingService)
    {
        _store = store;
        _trackingService = trackingService;
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
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        SetBusy(true);
        try
        {
            _categories = await _store.GetCategoriesAsync();
            _rules = await _store.GetRulesAsync();
            CategoryList.ItemsSource = _categories.Select(category => new CategoryDisplayItem(
                category,
                new SolidColorBrush(ParseColor(category.Color)),
                category.IsDefault ? "默认分类 · 未命中规则的活动" : $"排序 {category.SortOrder} · {category.Color}"));
            var categoriesById = _categories.ToDictionary(category => category.Id);
            RuleList.ItemsSource = _rules.Select(rule =>
            {
                categoriesById.TryGetValue(rule.CategoryId, out var category);
                return new RuleDisplayItem(
                    rule,
                    category?.Name ?? "未知分类",
                    rule.IsEnabled ? "已启用" : "已停用",
                    $"优先级 {rule.Priority}",
                    DescribeRule(rule),
                    new SolidColorBrush(ParseColor(category?.Color ?? "#8A8886")));
            }).ToArray();
            EmptyRules.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnNewCategoryClicked(object sender, RoutedEventArgs args)
    {
        var category = await ShowCategoryDialogAsync(null);
        if (category is null)
        {
            return;
        }

        await _store.SaveCategoryAsync(category);
        await ReloadAsync();
        ShowMessage("分类已创建", InfoBarSeverity.Success);
    }

    private async void OnCategoryItemClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not CategoryDisplayItem item)
        {
            return;
        }

        var category = await ShowCategoryDialogAsync(item.Category);
        if (category is null)
        {
            return;
        }

        await _store.SaveCategoryAsync(category);
        await _trackingService.ReloadRulesAsync();
        await ReloadAsync();
        ShowMessage("分类已更新", InfoBarSeverity.Success);
    }

    private async void OnDeleteCategoryClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: CategoryDisplayItem item })
        {
            return;
        }

        if (item.Category.IsDefault)
        {
            ShowMessage("默认分类不能删除", InfoBarSeverity.Warning);
            return;
        }

        if (!await ConfirmAsync("删除分类", $"确定删除“{item.Category.Name}”吗？仍被历史活动使用的分类不能删除。"))
        {
            return;
        }

        try
        {
            if (await _store.DeleteCategoryAsync(item.Category.Id))
            {
                await _trackingService.ReloadRulesAsync();
                await ReloadAsync();
                ShowMessage("分类已删除", InfoBarSeverity.Success);
            }
        }
        catch (Exception exception)
        {
            ShowMessage($"删除失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void OnNewRuleClicked(object sender, RoutedEventArgs args)
    {
        var rule = await ShowRuleDialogAsync(null);
        if (rule is null)
        {
            return;
        }

        await _store.SaveRuleAsync(rule);
        await _trackingService.ReloadRulesAsync();
        await ReloadAsync();
        ShowMessage("规则已创建", InfoBarSeverity.Success);
    }

    private async void OnEditRuleClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: RuleDisplayItem item })
        {
            return;
        }

        var rule = await ShowRuleDialogAsync(item.Rule);
        if (rule is null)
        {
            return;
        }

        await _store.SaveRuleAsync(rule);
        await _trackingService.ReloadRulesAsync();
        await ReloadAsync();
        ShowMessage("规则已更新", InfoBarSeverity.Success);
    }

    private async void OnDeleteRuleClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: RuleDisplayItem item }
            || !await ConfirmAsync("删除规则", $"确定删除规则“{item.Rule.Pattern}”吗？"))
        {
            return;
        }

        await _store.DeleteRuleAsync(item.Rule.Id);
        await _trackingService.ReloadRulesAsync();
        await ReloadAsync();
        ShowMessage("规则已删除", InfoBarSeverity.Success);
    }

    private async void OnReclassifyClicked(object sender, RoutedEventArgs args)
    {
        if (!await ConfirmAsync(
                "重新分类历史记录",
                "将使用当前规则重新处理所有历史活动。手动修正过的分类不会被覆盖。"))
        {
            return;
        }

        SetBusy(true);
        try
        {
            var service = new HistoricalReclassificationService(_store, _classificationEngine);
            var changed = await service.ReclassifyAsync();
            ShowMessage($"重新分类完成，共更新 {changed} 条记录", InfoBarSeverity.Success);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<Category?> ShowCategoryDialogAsync(Category? existing)
    {
        var nameBox = new TextBox { Header = "名称", Text = existing?.Name ?? string.Empty };
        var colorBox = new TextBox { Header = "颜色（#RRGGBB）", Text = existing?.Color ?? "#4F6BED" };
        var orderBox = new NumberBox
        {
            Header = "排序",
            Value = existing?.SortOrder ?? 50,
            Minimum = 0,
            Maximum = 999,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
            TextWrapping = TextWrapping.Wrap
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(nameBox);
        content.Children.Add(colorBox);
        content.Children.Add(orderBox);
        content.Children.Add(errorText);

        var dialog = CreateDialog(existing is null ? "新建分类" : "编辑分类", content, "保存");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                errorText.Text = "分类名称不能为空";
                args.Cancel = true;
            }
            else if (!Regex.IsMatch(colorBox.Text, "^#[0-9a-fA-F]{6}$"))
            {
                errorText.Text = "颜色必须使用 #RRGGBB 格式";
                args.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return new Category(
            existing?.Id ?? 0,
            nameBox.Text.Trim(),
            colorBox.Text.ToUpperInvariant(),
            double.IsNaN(orderBox.Value) ? 50 : (int)orderBox.Value,
            existing?.IsDefault ?? false);
    }

    private async Task<ClassificationRule?> ShowRuleDialogAsync(ClassificationRule? existing)
    {
        var categoryBox = new ComboBox
        {
            Header = "分类",
            ItemsSource = _categories,
            DisplayMemberPath = nameof(Category.Name),
            SelectedItem = _categories.FirstOrDefault(category => category.Id == existing?.CategoryId) ?? _categories.First()
        };
        var targets = new[]
        {
            new EnumChoice<RuleTarget>(RuleTarget.ProcessName, "进程名称"),
            new EnumChoice<RuleTarget>(RuleTarget.WindowTitle, "窗口标题"),
            new EnumChoice<RuleTarget>(RuleTarget.ProcessNameOrWindowTitle, "进程名称或窗口标题")
        };
        var targetBox = new ComboBox
        {
            Header = "匹配范围",
            ItemsSource = targets,
            DisplayMemberPath = nameof(EnumChoice<RuleTarget>.Name),
            SelectedItem = targets.First(choice => choice.Value == (existing?.Target ?? RuleTarget.ProcessNameOrWindowTitle))
        };
        var matchTypes = new[]
        {
            new EnumChoice<RuleMatchType>(RuleMatchType.Contains, "包含文本"),
            new EnumChoice<RuleMatchType>(RuleMatchType.RegularExpression, "正则表达式")
        };
        var matchTypeBox = new ComboBox
        {
            Header = "匹配方式",
            ItemsSource = matchTypes,
            DisplayMemberPath = nameof(EnumChoice<RuleMatchType>.Name),
            SelectedItem = matchTypes.First(choice => choice.Value == (existing?.MatchType ?? RuleMatchType.Contains))
        };
        var patternBox = new TextBox
        {
            Header = "匹配内容",
            Text = existing?.Pattern ?? string.Empty,
            PlaceholderText = "例如 Code 或 ^Visual Studio.*"
        };
        var testBox = new TextBox
        {
            Header = "测试文本",
            PlaceholderText = "输入一个进程名称或窗口标题"
        };
        var testResult = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var testButton = new Button { Content = "测试匹配" };
        var priorityBox = new NumberBox
        {
            Header = "优先级（数值越大越先匹配）",
            Value = existing?.Priority ?? 10,
            Minimum = -999,
            Maximum = 999,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        var ignoreCaseSwitch = new ToggleSwitch
        {
            Header = "忽略大小写",
            IsOn = existing?.IgnoreCase ?? true
        };
        var enabledSwitch = new ToggleSwitch
        {
            Header = "启用规则",
            IsOn = existing?.IsEnabled ?? true
        };
        testButton.Click += (_, _) =>
        {
            var candidate = BuildRule();
            var validationError = _classificationEngine.ValidatePattern(candidate);
            if (validationError is not null)
            {
                testResult.Text = validationError;
                testResult.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                return;
            }

            var result = _classificationEngine.Classify(
                testBox.Text,
                testBox.Text,
                [candidate],
                defaultCategoryId: -1);
            var matched = result.RuleId.HasValue;
            testResult.Text = matched ? "匹配成功" : "未匹配";
            testResult.Foreground = new SolidColorBrush(
                matched ? Microsoft.UI.Colors.Green : Microsoft.UI.Colors.Orange);
        };
        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
            TextWrapping = TextWrapping.Wrap
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 400 };
        foreach (var element in new FrameworkElement[]
                 {
                     categoryBox, targetBox, matchTypeBox, patternBox, testBox, testButton, testResult,
                     priorityBox, ignoreCaseSwitch, enabledSwitch, errorText
                 })
        {
            content.Children.Add(element);
        }

        var dialog = CreateDialog(existing is null ? "新建规则" : "编辑规则", content, "保存");
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var candidate = BuildRule();
            var error = _classificationEngine.ValidatePattern(candidate);
            if (error is not null)
            {
                errorText.Text = error;
                args.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return BuildRule();

        ClassificationRule BuildRule()
        {
            return new ClassificationRule(
                existing?.Id ?? 0,
                ((Category)categoryBox.SelectedItem).Id,
                ((EnumChoice<RuleTarget>)targetBox.SelectedItem).Value,
                ((EnumChoice<RuleMatchType>)matchTypeBox.SelectedItem).Value,
                patternBox.Text,
                ignoreCaseSwitch.IsOn,
                double.IsNaN(priorityBox.Value) ? 0 : (int)priorityBox.Value,
                enabledSwitch.IsOn);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private ContentDialog CreateDialog(string title, object content, string primaryText)
    {
        return new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
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

    private static string DescribeRule(ClassificationRule rule)
    {
        var target = rule.Target switch
        {
            RuleTarget.ProcessName => "进程名称",
            RuleTarget.WindowTitle => "窗口标题",
            _ => "进程名称或窗口标题"
        };
        var match = rule.MatchType == RuleMatchType.RegularExpression ? "正则" : "包含";
        var casing = rule.IgnoreCase ? "忽略大小写" : "区分大小写";
        return $"{target} · {match} · {casing}";
    }

    private static Windows.UI.Color ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return Windows.UI.Color.FromArgb(byte.MaxValue, 138, 136, 134);
        }

        return Windows.UI.Color.FromArgb(byte.MaxValue, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    public sealed record CategoryDisplayItem(Category Category, Brush ColorBrush, string Description)
    {
        public string Name => Category.Name;
    }

    public sealed record RuleDisplayItem(
        ClassificationRule Rule,
        string CategoryName,
        string EnabledText,
        string PriorityText,
        string MatchDescription,
        Brush ColorBrush)
    {
        public string Pattern => Rule.Pattern;
    }

    public sealed record EnumChoice<T>(T Value, string Name) where T : struct, Enum;
}
