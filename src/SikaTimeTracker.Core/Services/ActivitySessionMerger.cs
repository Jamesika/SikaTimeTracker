using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Services;

public static class ActivitySessionMerger
{
    public static IReadOnlyList<ActivitySession> Build(
        IEnumerable<ActivitySegment> activities,
        TimeSpan maximumGap,
        TimeSpan minimumActivityDuration)
    {
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumGap, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumActivityDuration, TimeSpan.Zero);

        var sessions = new List<ActivitySession>();
        var latestEnd = DateTimeOffset.MinValue;
        var endBeforeSession = DateTimeOffset.MinValue;
        // Merge before filtering: even a hidden short activity or another category
        // must interrupt continuity. Overlapping records retain their own lanes.
        foreach (var activity in activities
                     .Where(item => item.EffectiveEndTimeUtc > item.StartTimeUtc)
                     .OrderBy(item => item.StartTimeUtc)
                     .ThenBy(item => item.Id))
        {
            var previous = sessions.Count > 0 ? sessions[^1] : null;
            if (maximumGap > TimeSpan.Zero
                && previous is not null
                && activity.StartTimeUtc >= previous.Activity.EffectiveEndTimeUtc
                && activity.StartTimeUtc - previous.Activity.EffectiveEndTimeUtc <= maximumGap
                && endBeforeSession <= previous.Activity.EffectiveEndTimeUtc
                && string.Equals(activity.ProcessName, previous.Activity.ProcessName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(activity.WebsiteDomain, previous.Activity.WebsiteDomain, StringComparison.OrdinalIgnoreCase)
                && activity.CategoryId == previous.Activity.CategoryId)
            {
                sessions[^1] = new ActivitySession(previous.Activity with
                {
                    EndTimeUtc = activity.EndTimeUtc,
                    LastHeartbeatUtc = activity.LastHeartbeatUtc,
                    WindowTitle = activity.WindowTitle,
                    IsManuallyClassified = previous.Activity.IsManuallyClassified || activity.IsManuallyClassified
                }, previous.SegmentCount + 1);
            }
            else
            {
                endBeforeSession = latestEnd;
                sessions.Add(new ActivitySession(activity, 1));
            }

            if (activity.EffectiveEndTimeUtc > latestEnd)
            {
                latestEnd = activity.EffectiveEndTimeUtc;
            }
        }

        return sessions
            .Where(session => ActivityDisplayPolicy.ShouldDisplay(session.Activity, minimumActivityDuration))
            .ToArray();
    }
}
