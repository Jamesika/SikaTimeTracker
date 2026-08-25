using System.Text.RegularExpressions;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Services;

public sealed class ProgramClassificationService
{
    private const int ManualProgramRulePriority = 2_000_000;
    private readonly IActivityStore _store;

    public ProgramClassificationService(IActivityStore store)
    {
        _store = store;
    }

    public async Task<int> AssignCategoryAsync(
        string processName,
        long categoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        var processPattern = $"^{Regex.Escape(processName)}$";
        var existingRule = (await _store.GetRulesAsync(cancellationToken)).FirstOrDefault(rule =>
            rule.Target == RuleTarget.ProcessName
            && rule.MatchType == RuleMatchType.RegularExpression
            && rule.IgnoreCase
            && string.Equals(rule.Pattern, processPattern, StringComparison.Ordinal));
        var savedRule = await _store.SaveRuleAsync(new ClassificationRule(
            existingRule?.Id ?? 0,
            categoryId,
            RuleTarget.ProcessName,
            RuleMatchType.RegularExpression,
            processPattern,
            IgnoreCase: true,
            Priority: ManualProgramRulePriority,
            IsEnabled: true), cancellationToken);
        return await _store.UpdateActivitiesClassificationByProcessAsync(
            processName,
            categoryId,
            savedRule.Id,
            isManual: true,
            cancellationToken);
    }

    public async Task<int> AssignWebsiteDomainCategoryAsync(
        string websiteDomain,
        long categoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(websiteDomain);
        var domainPattern = $"^{Regex.Escape(websiteDomain)}$";
        var existingRule = (await _store.GetRulesAsync(cancellationToken)).FirstOrDefault(rule =>
            rule.Target == RuleTarget.WindowTitle
            && rule.MatchType == RuleMatchType.RegularExpression
            && rule.IgnoreCase
            && string.Equals(rule.Pattern, domainPattern, StringComparison.Ordinal));
        var savedRule = await _store.SaveRuleAsync(new ClassificationRule(
            existingRule?.Id ?? 0,
            categoryId,
            RuleTarget.WindowTitle,
            RuleMatchType.RegularExpression,
            domainPattern,
            IgnoreCase: true,
            Priority: ManualProgramRulePriority,
            IsEnabled: true), cancellationToken);
        return await _store.UpdateActivitiesClassificationByWebsiteDomainAsync(
            websiteDomain,
            categoryId,
            savedRule.Id,
            isManual: true,
            cancellationToken);
    }
}
