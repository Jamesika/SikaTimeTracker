using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Contracts;

public interface IActivityStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default);

    Task<Category> SaveCategoryAsync(Category category, CancellationToken cancellationToken = default);

    Task<bool> DeleteCategoryAsync(long categoryId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClassificationRule>> GetRulesAsync(CancellationToken cancellationToken = default);

    Task<ClassificationRule> SaveRuleAsync(ClassificationRule rule, CancellationToken cancellationToken = default);

    Task DeleteRuleAsync(long ruleId, CancellationToken cancellationToken = default);

    Task<long> StartActivityAsync(ActivityDraft activity, CancellationToken cancellationToken = default);

    Task<bool> UpdateHeartbeatAsync(long activityId, DateTimeOffset heartbeatUtc, CancellationToken cancellationToken = default);

    Task<bool> StopActivityAsync(long activityId, DateTimeOffset endTimeUtc, CancellationToken cancellationToken = default);

    Task<bool> DeleteActivityAsync(long activityId, CancellationToken cancellationToken = default);

    Task<int> RecoverOpenActivitiesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivitySegment>> GetActivitiesAsync(
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivitySegment>> GetAllActivitiesAsync(CancellationToken cancellationToken = default);

    Task<int> DeleteAllActivitiesAsync(CancellationToken cancellationToken = default);

    Task<bool> UpdateActivityClassificationAsync(
        long activityId,
        long categoryId,
        long? ruleId,
        bool isManual,
        CancellationToken cancellationToken = default);

    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);

    Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default);
}
