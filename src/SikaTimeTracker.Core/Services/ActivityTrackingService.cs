using SikaTimeTracker.Core.Contracts;
using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Services;

public sealed class ActivityTrackingService : IAsyncDisposable
{
    private readonly IActivityStore _store;
    private readonly IForegroundWindowSource _windowSource;
    private readonly ISystemActivitySource _systemSource;
    private readonly ClassificationEngine _classificationEngine;
    private readonly ActivityTrackingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    private IReadOnlyList<ClassificationRule> _rules = [];
    private long _defaultCategoryId;
    private Task? _healthLoop;
    private long? _currentActivityId;
    private WindowSnapshot? _currentWindow;
    private DateTimeOffset _currentStartUtc;
    private DateTimeOffset _lastHeartbeatUtc;
    private DateTimeOffset _lastHealthCheckUtc;
    private bool _isStarted;
    private bool _isPaused;
    private bool _isIdle;
    private bool _isSystemInteractive;

    public ActivityTrackingService(
        IActivityStore store,
        IForegroundWindowSource windowSource,
        ISystemActivitySource systemSource,
        ClassificationEngine classificationEngine,
        ActivityTrackingOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _windowSource = windowSource;
        _systemSource = systemSource;
        _classificationEngine = classificationEngine;
        _options = options ?? new ActivityTrackingOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<TrackingStatus>? StatusChanged;

    public TrackingStatus Status => CreateStatus();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isStarted)
        {
            return;
        }

        await _store.InitializeAsync(cancellationToken);
        await _store.RecoverOpenActivitiesAsync(cancellationToken);
        await ReloadRulesAsync(cancellationToken);

        _isSystemInteractive = _systemSource.IsSessionInteractive;
        _lastHealthCheckUtc = _timeProvider.GetUtcNow();
        _windowSource.ForegroundWindowChanged += OnForegroundWindowChanged;
        _systemSource.SystemActivityChanged += OnSystemActivityChanged;
        _systemSource.Start();
        _windowSource.Start();
        _isStarted = true;
        _healthLoop = RunHealthLoopAsync(_shutdown.Token);

        if (_isSystemInteractive)
        {
            await ProcessForegroundWindowAsync(_windowSource.GetCurrentWindow(), cancellationToken);
        }

        RaiseStatusChanged();
    }

    public async Task ReloadRulesAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _store.GetCategoriesAsync(cancellationToken);
        _defaultCategoryId = categories.Single(category => category.IsDefault).Id;
        _rules = await _store.GetRulesAsync(cancellationToken);
    }

    public async Task SetPausedAsync(bool isPaused, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_isPaused == isPaused)
            {
                return;
            }

            _isPaused = isPaused;
            var now = _timeProvider.GetUtcNow();
            if (_isPaused)
            {
                await StopCurrentActivityAsync(now, cancellationToken);
            }
            else if (_isSystemInteractive && !_isIdle)
            {
                await StartWindowAsync(_windowSource.GetCurrentWindow(), now, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        RaiseStatusChanged();
    }

    public async Task ProcessForegroundWindowAsync(
        WindowSnapshot? snapshot,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_isStarted || _isPaused || _isIdle || !_isSystemInteractive)
            {
                return;
            }

            var observedAtUtc = snapshot?.ObservedAtUtc ?? _timeProvider.GetUtcNow();
            if (_currentWindow is not null
                && snapshot is not null
                && _currentWindow.RepresentsSameActivity(snapshot))
            {
                return;
            }

            await StopCurrentActivityAsync(observedAtUtc, cancellationToken);
            await StartWindowAsync(snapshot, observedAtUtc, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        RaiseStatusChanged();
    }

    public async Task ProcessSystemActivityAsync(
        SystemActivityChangedEventArgs change,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _isSystemInteractive = change.IsInteractive;
            if (!_isSystemInteractive)
            {
                await StopCurrentActivityAsync(change.ObservedAtUtc, cancellationToken);
            }
            else if (!_isPaused)
            {
                _isIdle = IsIdle();
                if (!_isIdle)
                {
                    await StartWindowAsync(
                        _windowSource.GetCurrentWindow(),
                        change.ObservedAtUtc,
                        cancellationToken);
                }
            }

            _lastHealthCheckUtc = change.ObservedAtUtc;
        }
        finally
        {
            _gate.Release();
        }

        RaiseStatusChanged();
    }

    public async Task ProcessHealthCheckAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_isStarted)
            {
                return;
            }

            var observedGap = nowUtc - _lastHealthCheckUtc;
            if (_currentActivityId.HasValue && observedGap > _options.MaximumTrustedGap)
            {
                await StopCurrentActivityAsync(_lastHeartbeatUtc, cancellationToken);
            }

            _lastHealthCheckUtc = nowUtc;

            if (_isPaused || !_isSystemInteractive)
            {
                return;
            }

            var idleDuration = _options.IdleDetectionEnabled
                ? _systemSource.GetIdleDuration()
                : TimeSpan.Zero;
            var isNowIdle = _options.IdleDetectionEnabled && idleDuration >= _options.IdleThreshold;
            if (isNowIdle)
            {
                if (!_isIdle)
                {
                    _isIdle = true;
                    var idleBoundary = nowUtc - idleDuration + _options.IdleThreshold;
                    await StopCurrentActivityAsync(idleBoundary, cancellationToken);
                }

                return;
            }

            if (_isIdle)
            {
                _isIdle = false;
                await StartWindowAsync(_windowSource.GetCurrentWindow(), nowUtc, cancellationToken);
            }
            else
            {
                var snapshot = _windowSource.GetCurrentWindow();
                if (_currentWindow is null
                    || snapshot is null
                    || !_currentWindow.RepresentsSameActivity(snapshot))
                {
                    await StopCurrentActivityAsync(nowUtc, cancellationToken);
                    await StartWindowAsync(snapshot, nowUtc, cancellationToken);
                }
                else if (_currentActivityId.HasValue
                         && nowUtc - _lastHeartbeatUtc >= _options.HeartbeatInterval)
                {
                    if (await _store.UpdateHeartbeatAsync(_currentActivityId.Value, nowUtc, cancellationToken))
                    {
                        _lastHeartbeatUtc = nowUtc;
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        RaiseStatusChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_isStarted)
        {
            _gate.Dispose();
            _shutdown.Dispose();
            _windowSource.Dispose();
            _systemSource.Dispose();
            return;
        }

        _shutdown.Cancel();
        _windowSource.ForegroundWindowChanged -= OnForegroundWindowChanged;
        _systemSource.SystemActivityChanged -= OnSystemActivityChanged;
        _windowSource.Stop();
        _systemSource.Stop();

        if (_healthLoop is not null)
        {
            try
            {
                await _healthLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _gate.WaitAsync();
        try
        {
            await StopCurrentActivityAsync(_timeProvider.GetUtcNow(), CancellationToken.None);
            _isStarted = false;
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
        _shutdown.Dispose();
        _windowSource.Dispose();
        _systemSource.Dispose();
    }

    private async Task RunHealthLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.PollInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await RunSafelyAsync(() => ProcessHealthCheckAsync(_timeProvider.GetUtcNow(), cancellationToken));
        }
    }

    private async Task StartWindowAsync(
        WindowSnapshot? snapshot,
        DateTimeOffset startUtc,
        CancellationToken cancellationToken)
    {
        if (snapshot is null || _currentActivityId.HasValue)
        {
            return;
        }

        var classification = _classificationEngine.Classify(
            snapshot.ProcessName,
            snapshot.WindowTitle,
            _rules,
            _defaultCategoryId);
        _currentActivityId = await _store.StartActivityAsync(new ActivityDraft(
            startUtc,
            snapshot.ProcessName,
            snapshot.WindowTitle,
            classification.CategoryId,
            classification.RuleId), cancellationToken);
        _currentWindow = snapshot;
        _currentStartUtc = startUtc;
        _lastHeartbeatUtc = startUtc;
    }

    private async Task StopCurrentActivityAsync(
        DateTimeOffset requestedEndUtc,
        CancellationToken cancellationToken)
    {
        if (!_currentActivityId.HasValue)
        {
            _currentWindow = null;
            return;
        }

        var activityId = _currentActivityId.Value;
        var endUtc = requestedEndUtc < _currentStartUtc ? _currentStartUtc : requestedEndUtc;
        if (endUtc - _currentStartUtc < _options.MinimumActivityDuration)
        {
            await _store.DeleteActivityAsync(activityId, cancellationToken);
        }
        else
        {
            await _store.StopActivityAsync(activityId, endUtc, cancellationToken);
        }

        _currentActivityId = null;
        _currentWindow = null;
    }

    private bool IsIdle()
    {
        return _options.IdleDetectionEnabled
               && _systemSource.GetIdleDuration() >= _options.IdleThreshold;
    }

    private void OnForegroundWindowChanged(object? sender, WindowChangedEventArgs args)
    {
        _ = RunSafelyAsync(() => ProcessForegroundWindowAsync(args.Snapshot, _shutdown.Token));
    }

    private void OnSystemActivityChanged(object? sender, SystemActivityChangedEventArgs args)
    {
        _ = RunSafelyAsync(() => ProcessSystemActivityAsync(args, _shutdown.Token));
    }

    private static async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private TrackingStatus CreateStatus()
    {
        var statusText = !_isSystemInteractive
            ? "电脑已锁定或休眠"
            : _isPaused
                ? "已暂停"
                : _isIdle
                    ? "空闲中"
                    : _currentActivityId.HasValue
                        ? "正在追踪"
                        : "等待活动窗口";
        return new TrackingStatus(
            _currentActivityId.HasValue,
            _isPaused,
            _isIdle,
            _isSystemInteractive,
            statusText,
            _currentWindow);
    }

    private void RaiseStatusChanged()
    {
        StatusChanged?.Invoke(this, CreateStatus());
    }
}
