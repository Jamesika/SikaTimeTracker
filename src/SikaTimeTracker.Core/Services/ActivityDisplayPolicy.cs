using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Services;

public static class ActivityDisplayPolicy
{
    public static bool ShouldDisplay(
        ActivitySegment activity,
        TimeSpan minimumActivityDuration)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (minimumActivityDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumActivityDuration));
        }

        return !ProcessExclusionPolicy.ShouldExclude(activity.ProcessName)
               && activity.Duration >= minimumActivityDuration;
    }
}
