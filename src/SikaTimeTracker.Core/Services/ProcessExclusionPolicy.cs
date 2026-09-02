namespace SikaTimeTracker.Core.Services;

public static class ProcessExclusionPolicy
{
    private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "LockApp",
        "SikaTimeTracker"
    };

    public static bool ShouldExclude(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var normalizedName = System.IO.Path.GetFileNameWithoutExtension(processName.Trim());
        return ExcludedProcessNames.Contains(normalizedName);
    }
}
