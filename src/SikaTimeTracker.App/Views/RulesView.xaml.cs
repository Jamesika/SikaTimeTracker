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
            CategoryRuleList.ItemsSource = _categories.Select(category =>
            {
                var patterns = category.IsDefault
                    ? []
                    : _rules
                        .Where(rule => rule.CategoryId == category.Id && rule.IsEnabled)
                        .OrderByDescending(rule => rule.Priority)
                        .ThenBy(rule => rule.Id)
                        .Select(NormalizePattern)
                        .ToArray();
                var description = category.IsDefault
                    ? "默认分类 · 自动接收未命中其他规则的活动"
                    : $"{patterns.Length} 条正则 · 排序 {category.SortOrder}";
                return new CategoryRulesDisplayItem(
                    category,
                    new SolidColorBrush(ParseColor(category.Color)),
                    patterns,
                    description);
            }).ToArray();
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
        ShowMessage("分类已创建，可以直接为它添加正则", InfoBarSeverity.Success);
    }

    private async void OnEditCategoryClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: CategoryRulesDisplayItem item })
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

    private async void OnEditCategoryRulesClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: CategoryRulesDisplayItem item })
        {
            return;
        }

        var patternBox = new TextBox
        {
            Header = $"{item.Category.Name}的正则列表（每行一个）",
            PlaceholderText = "unity\nrider\ncode\nvisual studio",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono"),
            MinWidth = 460,
            Height = 260
        };
        patternBox.Text = string.Join(Environment.NewLine, item.Patterns);
        var helpText = new TextBlock
        {
            Text = "正则会同时匹配进程名称和窗口标题，忽略大小写；列表顺序越靠前越优先。",
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap
        };
        var errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
            TextWrapping = TextWrapping.Wrap
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(patternBox);
        content.Children.Add(helpText);
        content.Children.Add(errorText);
        var dialog = CreateDialog($"编辑“{item.Category.Name}”规则", content, "保存规则");
        dialog.PrimaryButtonClick += (_, clickArgs) =>
        {
            var patterns = ParsePatterns(patternBox.Text);
            for (var index = 0; index < patterns.Count; index++)
            {
                var candidate = CreateRegexRule(item.Category, patterns[index], index);
                var error = _classificationEngine.ValidatePattern(candidate);
                if (error is null)
                {
                    continue;
                }

                errorText.Text = $"第 {index + 1} 行无效：{error}";
                clickArgs.Cancel = true;
                return;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var patterns = ParsePatterns(patternBox.Text);
            foreach (var existing in _rules.Where(rule => rule.CategoryId == item.Category.Id))
            {
                await _store.DeleteRuleAsync(existing.Id);
            }

            for (var index = 0; index < patterns.Count; index++)
            {
                await _store.SaveRuleAsync(CreateRegexRule(item.Category, patterns[index], index));
            }

            await _trackingService.ReloadRulesAsync();
            var changed = await new HistoricalReclassificationService(_store, _classificationEngine)
                .ReclassifyAsync();
            await ReloadAsync();
            ShowMessage($"正则列表已保存，并更新 {changed} 条历史记录", InfoBarSeverity.Success);
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

    private async void OnDeleteCategoryClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: CategoryRulesDisplayItem item } || !item.CanDelete)
        {
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

    private async void OnReclassifyClicked(object sender, RoutedEventArgs args)
    {
        if (!await ConfirmAsync(
                "重新分类历史记录",
                "将使用所有分类的当前正则列表重新处理历史活动；手动批量修改过的程序不会被覆盖。"))
        {
            return;
        }

        SetBusy(true);
        try
        {
            var changed = await new HistoricalReclassificationService(_store, _classificationEngine)
                .ReclassifyAsync();
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
            Header = "分类顺序（数值越小，规则越优先）",
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
        dialog.PrimaryButtonClick += (_, clickArgs) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                errorText.Text = "分类名称不能为空";
                clickArgs.Cancel = true;
            }
            else if (!Regex.IsMatch(colorBox.Text, "^#[0-9a-fA-F]{6}$"))
            {
                errorText.Text = "颜色必须使用 #RRGGBB 格式";
                clickArgs.Cancel = true;
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

    private static ClassificationRule CreateRegexRule(Category category, string pattern, int index)
    {
        return new ClassificationRule(
            0,
            category.Id,
            RuleTarget.ProcessNameOrWindowTitle,
            RuleMatchType.RegularExpression,
            pattern,
            IgnoreCase: true,
            Priority: 1_000_000 - category.SortOrder * 1_000 - index,
            IsEnabled: true);
    }

    private static IReadOnlyList<string> ParsePatterns(string text)
    {
        return text
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .Select(pattern => pattern.Trim())
            .Where(pattern => pattern.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizePattern(ClassificationRule rule)
    {
        return rule.MatchType == RuleMatchType.RegularExpression
            ? rule.Pattern
            : Regex.Escape(rule.Pattern);
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

    private static Windows.UI.Color ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return Windows.UI.Color.FromArgb(byte.MaxValue, 138, 136, 134);
        }

        return Windows.UI.Color.FromArgb(byte.MaxValue, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    public sealed record CategoryRulesDisplayItem(
        Category Category,
        Brush ColorBrush,
        IReadOnlyList<string> Patterns,
        string Description)
    {
        public string Name => Category.Name;

        public bool CanDelete => !Category.IsDefault;

        public Visibility EditVisibility => Category.IsDefault ? Visibility.Collapsed : Visibility.Visible;

        public Visibility RulesVisibility => EditVisibility;

        public string EmptyText => Patterns.Count == 0 ? "尚未设置正则。" : string.Empty;
    }
}
