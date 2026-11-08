using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NovaSetup;

/// <summary>安装向导统一的配色（浅色 Fluent 风格，和主程序的强调色一致）。</summary>
internal static class Palette
{
    public static readonly Color Window = Color.FromArgb(0xF5, 0xF7, 0xFA);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(0xE2, 0xE6, 0xEB);
    public static readonly Color TextPrimary = Color.FromArgb(0x1A, 0x1C, 0x20);
    public static readonly Color TextSecondary = Color.FromArgb(0x6C, 0x72, 0x7C);
    public static readonly Color Accent = Color.FromArgb(0x0F, 0x6C, 0xBD);
    public static readonly Color AccentHover = Color.FromArgb(0x11, 0x5E, 0xA3);
    public static readonly Color AccentPressed = Color.FromArgb(0x0C, 0x48, 0x7E);
    public static readonly Color Track = Color.FromArgb(0xE8, 0xEB, 0xEF);
    public static readonly Color ConsoleBack = Color.FromArgb(0x18, 0x1A, 0x1F);
    public static readonly Color ConsoleText = Color.FromArgb(0xD6, 0xDB, 0xE2);
    public static readonly Color Success = Color.FromArgb(0x10, 0x7C, 0x48);
}

internal static class Round
{
    public static GraphicsPath Make(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();

        if (radius <= 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        float d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>白底圆角卡片，外面露出窗口底色。</summary>
internal sealed class CardPanel : Panel
{
    public CardPanel()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        BackColor = Palette.Window;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Palette.Window);

        var rect = new RectangleF(0, 0, Width - 1, Height - 1);
        using GraphicsPath path = Round.Make(rect, 8);
        using var fill = new SolidBrush(Palette.Card);
        using var pen = new Pen(Palette.Border, 1);

        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>圆角按钮：Filled = 主按钮（强调色实心），否则是描边次要按钮。</summary>
internal sealed class ModernButton : Button
{
    private bool _hover;
    private bool _press;

    public ModernButton()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = new Font("Microsoft YaHei UI", 9.5F);
        Cursor = Cursors.Hand;
        BackColor = Palette.Window;
    }

    /// <summary>主按钮用强调色实心。</summary>
    [DefaultValue(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Filled { get; set; }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _press = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _press = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _press = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var rect = new RectangleF(0, 0, Width, Height);
        using GraphicsPath path = Round.Make(rect, 6);

        Color fill;
        Color text;

        if (!Enabled)
        {
            fill = Palette.Track;
            text = Palette.TextSecondary;
        }
        else if (Filled)
        {
            fill = _press ? Palette.AccentPressed : _hover ? Palette.AccentHover : Palette.Accent;
            text = Color.White;
        }
        else
        {
            fill = _press ? Color.FromArgb(0xE9, 0xED, 0xF2) : _hover ? Color.White : Palette.Card;
            text = Palette.TextPrimary;
        }

        using (var brush = new SolidBrush(fill))
        {
            g.FillPath(brush, path);
        }

        if (!Filled && Enabled)
        {
            using var pen = new Pen(Palette.Border, 1);
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            new Rectangle(0, 0, Width, Height),
            text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>圆角进度条（0-100）。</summary>
internal sealed class AccentProgressBar : Control
{
    private int _value;

    public AccentProgressBar()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        BackColor = Palette.Window;
    }

    [DefaultValue(0)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            int next = Math.Clamp(value, 0, 100);

            if (next != _value)
            {
                _value = next;
                Invalidate();
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        float radius = Height / 2f;
        var track = new RectangleF(0, 0, Width, Height);

        using (GraphicsPath path = Round.Make(track, radius))
        using (var brush = new SolidBrush(Palette.Track))
        {
            g.FillPath(brush, path);
        }

        if (_value <= 0)
        {
            return;
        }

        float width = Math.Max(Height, Width * _value / 100f);
        var filled = new RectangleF(0, 0, width, Height);

        using (GraphicsPath path = Round.Make(filled, radius))
        using (var brush = new SolidBrush(Palette.Accent))
        {
            g.FillPath(brush, path);
        }
    }
}

/// <summary>安装向导右上角的装饰图形：圆角方块里的下载箭头。</summary>
internal sealed class InstallGlyph : Control
{
    public InstallGlyph()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        BackColor = Palette.Card;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        float s = Math.Min(Width, Height);
        var rect = new RectangleF(0.5f, 0.5f, s - 1, s - 1);

        using (GraphicsPath path = Round.Make(rect, s * 0.18f))
        using (var fill = new SolidBrush(Color.FromArgb(0xEF, 0xF5, 0xFC)))
        using (var pen = new Pen(Color.FromArgb(0xD3, 0xE3, 0xF6), 1))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        float cx = s / 2f;
        float top = s * 0.22f;
        float bottom = s * 0.62f;
        float stroke = Math.Max(2f, s * 0.05f);

        using (var accent = new SolidBrush(Palette.Accent))
        using (var accentPen = new Pen(Palette.Accent, stroke))
        {
            g.DrawLine(accentPen, cx, top, cx, bottom - s * 0.12f);

            PointF[] head =
            {
                new(cx - s * 0.13f, bottom - s * 0.16f),
                new(cx + s * 0.13f, bottom - s * 0.16f),
                new(cx, bottom),
            };
            g.FillPolygon(accent, head);
        }

        using var tray = new Pen(Color.FromArgb(0xA9, 0xC6, 0xE8), Math.Max(2f, s * 0.045f));
        g.DrawLine(tray, s * 0.27f, s * 0.74f, s * 0.73f, s * 0.74f);
    }
}

/// <summary>完成页的绿色对勾徽章。</summary>
internal sealed class CheckBadge : Control
{
    public CheckBadge()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,
            true);
        BackColor = Palette.Card;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var circle = new RectangleF(0, 0, Width - 1, Height - 1);

        using (var brush = new SolidBrush(Palette.Success))
        {
            g.FillEllipse(brush, circle);
        }

        using var pen = new Pen(Color.White, Math.Max(2f, Width / 14f));
        PointF[] points =
        {
            new PointF(Width * 0.28f, Height * 0.52f),
            new PointF(Width * 0.44f, Height * 0.68f),
            new PointF(Width * 0.73f, Height * 0.34f),
        };
        g.DrawLines(pen, points);
    }
}
