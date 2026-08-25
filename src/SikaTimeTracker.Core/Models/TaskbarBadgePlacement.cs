namespace SikaTimeTracker.Core.Models;

public enum TaskbarEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3
}

public readonly record struct PixelBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);
}

public readonly record struct TaskbarBadgePlacement(
    int X,
    int Y,
    int Width,
    int Height,
    bool IsCompact);
