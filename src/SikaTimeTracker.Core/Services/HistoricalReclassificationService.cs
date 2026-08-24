using SikaTimeTracker.Core.Contracts;

namespace SikaTimeTracker.Core.Services;

public sealed class HistoricalReclassificationService
{
    private readonly IActivityStore _store;
    private readonly ClassificationEngine _classificationEngine;

    public HistoricalReclassificationService(
        IActivityStore store,
        ClassificationEngine classificationEngine)
    {
        _store = store;
        _classificationEngine = classificationEngine;
    }

    public async Task<int> ReclassifyAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _store.GetCategoriesAsync(cancellationToken);
        var defaultCategoryId = categories.Single(category => category.IsDefault).Id;
        var rules = await _store.GetRulesAsync(cancellationToken);
        var activities = await _store.GetAllActivitiesAsync(cancellationToken);
        var changed = 0;

        foreach (var activity in activities.Where(activity => !activity.IsManuallyClassified))
        {
            var result = _classificationEngine.Classify(
                activity.ProcessName,
                activity.WindowTitle,
                rules,
                defaultCategoryId);
            if (activity.CategoryId == result.CategoryId
                && activity.ClassificationRuleId == result.RuleId)
            {
                continue;
            }

            if (await _store.UpdateActivityClassificationAsync(
                    activity.Id,
                    result.CategoryId,
                    result.RuleId,
                    isManual: false,
                    cancellationToken))
            {
                changed++;
            }
        }

        return changed;
    }
}
