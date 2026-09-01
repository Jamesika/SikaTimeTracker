using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace SikaTimeTracker.Services;

internal sealed record TrayMenuEntry(string Text, Action? Action);

/// <summary>
/// 自绘现代扁平托盘菜单（Fluent 风格）：圆角、深浅主题、hover 高亮、淡入，
/// 替代 WinForms ContextMenuStrip 的老式外观。
/// </summary>
internal sealed class ModernTrayMenuForm : Forms.Form
{
    private const int CornerRadius = 8;
    private const double FadeInStep = 0.16;
    private const int FadeInIntervalMs = 15;
    private const int CloseDelayMs = 300;
    private readonly IReadOnlyList<TrayMenuEntry> _entries;
    private readonly Rectangle[] _itemBounds;
    private readonly Forms.Timer _fadeTimer;
    private readonly Forms.Timer _closeTimer;
    private bool _useLightPalette;
    private int _hoverIndex = -1;

    public ModernTrayMenuForm(bool useLightPalette, IReadOnlyList<TrayMenuEntry> entries)
    {
        _entries = entries;
        _useLightPalette = useLightPalette;

        var scale = Math.Max(1, DeviceDpi) / 96d;
        var itemHeight = (int)Math.Round(36 * scale);
        var separatorHeight = (int)Math.Round(9 * scale);
        var itemWidth = (int)Math.Round(210 * scale);
        var padding = (int)Math.Round(6 * scale);

        _itemBounds = new Rectangle[entries.Count];
        var y = padding;
        for (var i = 0; i < entries.Count; i++)
        {
            var height = entries[i].Action is null ? separatorHeight : itemHeight;
            _itemBounds[i] = new Rectangle(padding, y, itemWidth - (padding * 2), height);
            y += height;
        }

        AutoScaleMode = Forms.AutoScaleMode.None;
        FormBorderStyle = Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(itemWidth, y + padding);
        BackColor = BackgroundColor;
        SetStyle(
            Forms.ControlStyles.AllPaintingInWmPaint
            | Forms.ControlStyles.OptimizedDoubleBuffer
            | Forms.ControlStyles.ResizeRedraw
            | Forms.ControlStyles.UserPaint,
            true);
        KeyPreview = true;

        _fadeTimer = new Forms.Timer { Interval = FadeInIntervalMs };
        _fadeTimer.Tick += OnFadeTimerTick;
        _closeTimer = new Forms.Timer { Interval = CloseDelayMs };
        _closeTimer.Tick += OnCloseTimerTick;
    }

    public void ShowAt(System.Drawing.Point screenPoint)
    {
        var workingArea = Forms.Screen.FromPoint(screenPoint).WorkingArea;
        const int margin = 8;
        // 优先在光标上方弹出（与原生托盘菜单一致）；上方空间不足才改下方
        var y = screenPoint.Y - Height - margin;
        if (y < workingArea.Top)
        {
            y = screenPoint.Y + margin;
        }

        y = Math.Clamp(y, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height));
        // X：左对齐光标，右边缘越界则右对齐
        var x = screenPoint.X + Width > workingArea.Right
            ? screenPoint.X - Width
            : screenPoint.X;
        x = Math.Clamp(x, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width));
        Location = new System.Drawing.Point(x, y);
        Opacity = 0;
        Show();
        Activate();
        _fadeTimer.Start();
    }

    protected override void OnFormClosing(Forms.FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _fadeTimer.Stop();
        _closeTimer.Stop();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _closeTimer.Stop();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // 防抖：短暂失活（如激活失败）不关闭，持续失活（点击外部）才关闭
        _closeTimer.Stop();
        _closeTimer.Start();
    }

    protected override void OnKeyDown(Forms.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Forms.Keys.Escape)
        {
            Close();
        }
    }

    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateHover(HitTest(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        UpdateHover(-1);
    }

    protected override void OnMouseUp(Forms.MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var index = HitTest(e.Location);
        if (index >= 0 && index < _entries.Count && _entries[index].Action is { } action)
        {
            action();
            Close();
        }
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.Clear(BackgroundColor);
        var scale = Math.Max(1, DeviceDpi) / 96d;
        var textInset = (int)Math.Round(10 * scale);
        using var menuFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        for (var i = 0; i < _entries.Count; i++)
        {
            var bounds = _itemBounds[i];
            var entry = _entries[i];
            if (entry.Action is null)
            {
                using var lineBrush = new SolidBrush(SeparatorColor);
                graphics.FillRectangle(lineBrush, bounds.X, bounds.Y + (bounds.Height / 2), bounds.Width, 1);
                continue;
            }

            if (i == _hoverIndex)
            {
                using var hoverBrush = new SolidBrush(HoverColor);
                graphics.FillRectangle(hoverBrush, bounds);
            }

            Forms.TextRenderer.DrawText(
                graphics,
                entry.Text,
                menuFont,
                new Rectangle(bounds.X + textInset, bounds.Y, bounds.Width - textInset, bounds.Height),
                TextColor,
                Forms.TextFormatFlags.VerticalCenter
                | Forms.TextFormatFlags.Left
                | Forms.TextFormatFlags.NoPadding
                | Forms.TextFormatFlags.NoPrefix);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var previousRegion = Region;
        Region = CreateRoundedRegion(
            ClientRectangle,
            Math.Max(4, (int)Math.Round(CornerRadius * Math.Max(1, DeviceDpi) / 96d)));
        previousRegion?.Dispose();
    }

    private void UpdateHover(int index)
    {
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Invalidate();
        }
    }

    private int HitTest(System.Drawing.Point point)
    {
        for (var i = 0; i < _itemBounds.Length; i++)
        {
            if (_itemBounds[i].Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    private void OnFadeTimerTick(object? sender, EventArgs e)
    {
        Opacity = Math.Min(1, Opacity + FadeInStep);
        if (Opacity >= 1)
        {
            _fadeTimer.Stop();
        }
    }

    private void OnCloseTimerTick(object? sender, EventArgs e)
    {
        _closeTimer.Stop();
        Close();
    }

    private static Region CreateRoundedRegion(Rectangle bounds, int radius)
    {
        using var path = CreateRoundedPath(bounds, radius);
        return new Region(path);
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static readonly Color DarkBackground = Color.FromArgb(32, 32, 32);
    private static readonly Color DarkHover = Color.FromArgb(58, 58, 58);
    private static readonly Color DarkText = Color.FromArgb(243, 243, 243);
    private static readonly Color DarkSeparator = Color.FromArgb(58, 58, 58);
    private static readonly Color LightBackground = Color.FromArgb(246, 246, 246);
    private static readonly Color LightHover = Color.FromArgb(229, 229, 229);
    private static readonly Color LightText = Color.FromArgb(26, 26, 26);
    private static readonly Color LightSeparator = Color.FromArgb(229, 229, 229);

    private Color BackgroundColor => _useLightPalette ? LightBackground : DarkBackground;

    private Color HoverColor => _useLightPalette ? LightHover : DarkHover;

    private Color TextColor => _useLightPalette ? LightText : DarkText;

    private Color SeparatorColor => _useLightPalette ? LightSeparator : DarkSeparator;
}
