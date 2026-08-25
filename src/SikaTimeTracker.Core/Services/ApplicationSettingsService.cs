using System.Globalization;
using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Services;

public sealed class ApplicationSettingsService
{
    private readonly IActivityStore _store;

    public ApplicationSettingsService(IActivityStore store)
    {
        _store = store;
    }

    public async Task<AppPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        return new AppPreferences
        {
            RunAtStartup = await GetBoolAsync("RunAtStartup", false, cancellationToken),
            StartMinimized = await GetBoolAsync("StartMinimized", false, cancellationToken),
            IdleDetectionEnabled = await GetBoolAsync("IdleDetectionEnabled", true, cancellationToken),
            IdleThresholdMinutes = await GetIntAsync("IdleThresholdMinutes", 5, 1, 120, cancellationToken),
            MinimumActivitySeconds = await GetIntAsync("MinimumActivitySeconds", 30, 0, 60, cancellationToken),
            MergeGapSeconds = await GetIntAsync("MergeGapSeconds", 10, 0, 300, cancellationToken),
            RecordWindowTitles = await GetBoolAsync("RecordWindowTitles", true, cancellationToken),
            Theme = await GetEnumAsync("Theme", AppTheme.System, cancellationToken)
        };
    }

    public async Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
    {
        await _store.SetSettingAsync("RunAtStartup", preferences.RunAtStartup.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await _store.SetSettingAsync("StartMinimized", preferences.StartMinimized.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await _store.SetSettingAsync("IdleDetectionEnabled", preferences.IdleDetectionEnabled.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await _store.SetSettingAsync("IdleThresholdMinutes", preferences.IdleThresholdMinutes.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await _store.SetSettingAsync("MinimumActivitySeconds", preferences.MinimumActivitySeconds.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await _store.SetSettingAsync("MergeGapSeconds", preferences.MergeGapSeconds.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await _store.SetSettingAsync("RecordWindowTitles", preferences.RecordWindowTitles.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await _store.SetSettingAsync("Theme", preferences.Theme.ToString(), cancellationToken);
    }

    private async Task<bool> GetBoolAsync(
        string key,
        bool fallback,
        CancellationToken cancellationToken)
    {
        var value = await _store.GetSettingAsync(key, cancellationToken);
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private async Task<int> GetIntAsync(
        string key,
        int fallback,
        int minimum,
        int maximum,
        CancellationToken cancellationToken)
    {
        var value = await _store.GetSettingAsync(key, cancellationToken);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }

    private async Task<T> GetEnumAsync<T>(
        string key,
        T fallback,
        CancellationToken cancellationToken)
        where T : struct, Enum
    {
        var value = await _store.GetSettingAsync(key, cancellationToken);
        return Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
    }
}
