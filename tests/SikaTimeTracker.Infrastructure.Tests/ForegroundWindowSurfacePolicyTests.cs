using SikaTimeTracker.Infrastructure.Tracking;

namespace SikaTimeTracker.Infrastructure.Tests;

[TestClass]
public sealed class ForegroundWindowSurfacePolicyTests
{
    [TestMethod]
    public void TitledVisibleWindow_IsUserSurface()
    {
        Assert.IsTrue(ForegroundWindowSurfacePolicy.IsLikelyUserSurface(CreateSurface()));
    }

    [TestMethod]
    public void UntitledSmallWindow_IsNotUserSurface()
    {
        Assert.IsFalse(ForegroundWindowSurfacePolicy.IsLikelyUserSurface(CreateSurface(
            hasTitle: false,
            width: 320,
            height: 120)));
    }

    [TestMethod]
    public void UntitledFullscreenWindow_IsUserSurface()
    {
        Assert.IsTrue(ForegroundWindowSurfacePolicy.IsLikelyUserSurface(CreateSurface(
            hasTitle: false,
            width: 1920,
            height: 1080)));
    }

    [TestMethod]
    public void ExplicitAppWindowWithoutTitle_IsUserSurface()
    {
        Assert.IsTrue(ForegroundWindowSurfacePolicy.IsLikelyUserSurface(CreateSurface(
            isAppWindow: true,
            hasTitle: false,
            width: 400,
            height: 240)));
    }

    [TestMethod]
    public void ToolWindowWithoutAppWindowStyle_IsNotUserSurface()
    {
        Assert.IsFalse(ForegroundWindowSurfacePolicy.IsLikelyUserSurface(CreateSurface(
            isToolWindow: true)));
    }

    [TestMethod]
    public void NonInteractiveOrInvisibleWindows_AreNotUserSurfaces()
    {
        Assert.IsFalse(ForegroundWindowSurfacePolicy.IsLikelyUserSurface(CreateSurface(isVisible: false)));
        Assert.IsFalse(ForegroundWindowSurfacePolicy.IsLikelyUserSurface(CreateSurface(isMinimized: true)));
        Assert.IsFalse(ForegroundWindowSurfacePolicy.IsLikelyUserSurface(CreateSurface(isCloaked: true)));
        Assert.IsFalse(ForegroundWindowSurfacePolicy.IsLikelyUserSurface(CreateSurface(isNoActivate: true)));
        Assert.IsFalse(ForegroundWindowSurfacePolicy.IsLikelyUserSurface(CreateSurface(isTransparentLayered: true)));
    }

    private static ForegroundWindowSurfaceInfo CreateSurface(
        bool isVisible = true,
        bool isMinimized = false,
        bool isCloaked = false,
        bool isNoActivate = false,
        bool isTransparentLayered = false,
        bool isToolWindow = false,
        bool isAppWindow = false,
        bool hasTitle = true,
        int width = 1280,
        int height = 720)
    {
        return new ForegroundWindowSurfaceInfo(
            isVisible,
            isMinimized,
            isCloaked,
            IsChild: false,
            IsDisabled: false,
            isNoActivate,
            isTransparentLayered,
            isToolWindow,
            isAppWindow,
            hasTitle,
            width,
            height);
    }
}
