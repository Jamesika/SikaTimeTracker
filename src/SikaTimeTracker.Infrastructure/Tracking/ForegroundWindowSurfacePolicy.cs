namespace SikaTimeTracker.Infrastructure.Tracking;

public static class ForegroundWindowSurfacePolicy
{
    private const int MinimumWindowWidth = 64;
    private const int MinimumWindowHeight = 48;
    private const int MinimumUntitledWindowWidth = 640;
    private const int MinimumUntitledWindowHeight = 360;

    public static bool IsLikelyUserSurface(ForegroundWindowSurfaceInfo surface)
    {
        if (!surface.IsVisible
            || surface.IsMinimized
            || surface.IsCloaked
            || surface.IsChild
            || surface.IsDisabled
            || surface.IsNoActivate
            || surface.IsTransparentLayered
            || surface.Width < MinimumWindowWidth
            || surface.Height < MinimumWindowHeight)
        {
            return false;
        }

        if (surface.IsToolWindow && !surface.IsAppWindow)
        {
            return false;
        }

        return surface.HasTitle
               || surface.IsAppWindow
               || surface.Width >= MinimumUntitledWindowWidth
               && surface.Height >= MinimumUntitledWindowHeight;
    }
}

public readonly record struct ForegroundWindowSurfaceInfo(
    bool IsVisible,
    bool IsMinimized,
    bool IsCloaked,
    bool IsChild,
    bool IsDisabled,
    bool IsNoActivate,
    bool IsTransparentLayered,
    bool IsToolWindow,
    bool IsAppWindow,
    bool HasTitle,
    int Width,
    int Height);
