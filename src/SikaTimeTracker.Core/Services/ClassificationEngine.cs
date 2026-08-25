using System.Text.RegularExpressions;
using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Services;

public sealed class ClassificationEngine
{
    public static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan _regexTimeout;

    public ClassificationEngine(TimeSpan? regexTimeout = null)
    {
        _regexTimeout = regexTimeout ?? DefaultRegexTimeout;
    }

    public ClassificationResult Classify(
        string processName,
        string windowTitle,
        IEnumerable<ClassificationRule> rules,
        long defaultCategoryId,
        string websiteDomain = "")
    {
        foreach (var rule in rules
                     .Where(rule => rule.IsEnabled)
                     .OrderByDescending(rule => rule.Priority)
                     .ThenBy(rule => rule.Id))
        {
            try
            {
                if (IsMatch(rule, processName, windowTitle, websiteDomain))
                {
                    return new ClassificationResult(rule.CategoryId, rule.Id);
                }
            }
            catch (ArgumentException exception)
            {
                return new ClassificationResult(defaultCategoryId, null, exception.Message);
            }
            catch (RegexMatchTimeoutException)
            {
                return new ClassificationResult(defaultCategoryId, null, "正则表达式匹配超时");
            }
        }

        return new ClassificationResult(defaultCategoryId, null);
    }

    public string? ValidatePattern(ClassificationRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Pattern))
        {
            return "匹配内容不能为空";
        }

        if (rule.MatchType != RuleMatchType.RegularExpression)
        {
            return null;
        }

        try
        {
            _ = new Regex(rule.Pattern, GetRegexOptions(rule), _regexTimeout);
            return null;
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
    }

    private bool IsMatch(
        ClassificationRule rule,
        string processName,
        string windowTitle,
        string websiteDomain)
    {
        return rule.Target switch
        {
            RuleTarget.ProcessName => IsTextMatch(rule, processName),
            RuleTarget.WindowTitle => IsTextMatch(rule, windowTitle) || IsTextMatch(rule, websiteDomain),
            RuleTarget.ProcessNameOrWindowTitle =>
                IsTextMatch(rule, processName)
                || IsTextMatch(rule, windowTitle)
                || IsTextMatch(rule, websiteDomain),
            _ => false
        };
    }

    private bool IsTextMatch(ClassificationRule rule, string value)
    {
        if (rule.MatchType == RuleMatchType.Contains)
        {
            var comparison = rule.IgnoreCase
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return value.Contains(rule.Pattern, comparison);
        }

        return Regex.IsMatch(value, rule.Pattern, GetRegexOptions(rule), _regexTimeout);
    }

    private static RegexOptions GetRegexOptions(ClassificationRule rule)
    {
        var options = RegexOptions.CultureInvariant;
        return rule.IgnoreCase ? options | RegexOptions.IgnoreCase : options;
    }
}
