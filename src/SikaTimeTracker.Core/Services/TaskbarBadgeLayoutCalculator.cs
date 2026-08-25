using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Services;

public static class TaskbarBadgeLayoutCalculator
{
    public static TaskbarBadgePlacement Calculate(
        PixelBounds taskbarBounds,
        TaskbarEdge edge,
        uint dpi)
    {
        if (taskbarBounds.Width <= 0 || taskbarBounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(taskbarBounds));
        }

        var scale = Math.Max(96u, dpi) / 96d;
        var margin = Math.Max(2, (int)Math.Round(6 * scale));
        if (edge is TaskbarEdge.Top or TaskbarEdge.Bottom)
        {
            var availableHeight = Math.Max(1, taskbarBounds.Height - (margin * 2));
            var desiredHeight = Math.Max(1, (int)Math.Round(34 * scale));
            var height = Math.Min(desiredHeight, availableHeight);
            var desiredWidth = Math.Max(1, (int)Math.Round(172 * scale));
            var width = Math.Min(desiredWidth, Math.Max(1, taskbarBounds.Width - (margin * 2)));
            return new TaskbarBadgePlacement(
                taskbarBounds.Left + margin,
                taskbarBounds.Top + ((taskbarBounds.Height - height) / 2),
                width,
                height,
                false);
        }

        var availableWidth = Math.Max(1, taskbarBounds.Width - (margin * 2));
        var compactWidth = Math.Min((int)Math.Round(42 * scale), availableWidth);
        var compactHeight = Math.Min(
            (int)Math.Round(64 * scale),
            Math.Max(1, taskbarBounds.Height - (margin * 2)));
        return new TaskbarBadgePlacement(
            taskbarBounds.Left + ((taskbarBounds.Width - compactWidth) / 2),
            taskbarBounds.Top + margin,
            compactWidth,
            compactHeight,
            true);
    }
}
