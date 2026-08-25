using SikaTimeTracker.Core.Models;
using SikaTimeTracker.Core.Services;

namespace SikaTimeTracker.Core.Tests;

[TestClass]
public sealed class TaskbarBadgeLayoutCalculatorTests
{
    [TestMethod]
    public void Calculate_BottomTaskbarCentersBadgeAtOneHundredPercentScale()
    {
        var placement = TaskbarBadgeLayoutCalculator.Calculate(
            new PixelBounds(0, 1032, 1920, 1080),
            TaskbarEdge.Bottom,
            96);

        Assert.AreEqual(6, placement.X);
        Assert.AreEqual(1039, placement.Y);
        Assert.AreEqual(172, placement.Width);
        Assert.AreEqual(34, placement.Height);
        Assert.IsFalse(placement.IsCompact);
    }

    [TestMethod]
    public void Calculate_ScalesForHighDpiTaskbar()
    {
        var placement = TaskbarBadgeLayoutCalculator.Calculate(
            new PixelBounds(0, 1368, 2560, 1440),
            TaskbarEdge.Bottom,
            144);

        Assert.AreEqual(9, placement.X);
        Assert.AreEqual(1378, placement.Y);
        Assert.AreEqual(258, placement.Width);
        Assert.AreEqual(51, placement.Height);
    }

    [TestMethod]
    public void Calculate_VerticalTaskbarUsesCompactLayout()
    {
        var placement = TaskbarBadgeLayoutCalculator.Calculate(
            new PixelBounds(0, 0, 56, 1080),
            TaskbarEdge.Left,
            96);

        Assert.IsTrue(placement.IsCompact);
        Assert.AreEqual(7, placement.X);
        Assert.AreEqual(6, placement.Y);
        Assert.AreEqual(42, placement.Width);
        Assert.AreEqual(64, placement.Height);
    }
}
