using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// 现代深色主题: 统一色板 / 字体 / 圆角 / 自绘按钮
internal static class UiTheme
{
    public static readonly Color Bg = Color.FromArgb(22, 23, 27);            // 窗口底
    public static readonly Color Surface = Color.FromArgb(30, 32, 38);       // 面板
    public static readonly Color SurfaceAlt = Color.FromArgb(38, 41, 48);    // 输入框/按钮
    public static readonly Color SurfaceHover = Color.FromArgb(46, 50, 58);
    public static readonly Color Border = Color.FromArgb(52, 56, 64);
    public static readonly Color BorderFocus = Color.FromArgb(96, 104, 116);
    public static readonly Color Text = Color.FromArgb(232, 234, 240);
    public static readonly Color TextDim = Color.FromArgb(154, 161, 172);
    public static readonly Color TextFaint = Color.FromArgb(107, 114, 128);
    public static readonly Color Good = Color.FromArgb(120, 210, 120);
    public static readonly Color Warn = Color.FromArgb(240, 180, 90);
    public static readonly Color Bad = Color.FromArgb(225, 130, 110);

    public static Font FontUi = new Font("Microsoft YaHei UI", 9.5f);
    public static Font FontUiSmall = new Font("Microsoft YaHei UI", 8.5f);
    public static Font FontUiBold = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
    public static Font FontMono = new Font("Consolas", 12f);
    public static Font FontMonoSmall = new Font("Consolas", 9.5f);

    public static Color Accent(Lcd1602Control.BacklightKind k)
    {
        if (k == Lcd1602Control.BacklightKind.Green) return Color.FromArgb(127, 211, 90);
        if (k == Lcd1602Control.BacklightKind.Off) return Color.FromArgb(138, 146, 156);
        return Color.FromArgb(111, 180, 255);
    }

    public static Color Mix(Color a, Color b, float k)
    {
        return Color.FromArgb(
            (int)(a.R * k + b.R * (1 - k)),
            (int)(a.G * k + b.G * (1 - k)),
            (int)(a.B * k + b.B * (1 - k)));
    }

    public static GraphicsPath Rounded(Rectangle r, int rad)
    {
        GraphicsPath p = new GraphicsPath();
        if (rad < 1) { p.AddRectangle(r); return p; }
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRounded(Graphics g, Rectangle r, int rad, Color c)
    {
        using (GraphicsPath p = Rounded(r, rad))
        using (SolidBrush b = new SolidBrush(c))
            g.FillPath(b, p);
    }

    public static void StrokeRounded(Graphics g, Rectangle r, int rad, Color c, float w)
    {
        using (GraphicsPath p = Rounded(r, rad))
        using (Pen pen = new Pen(c, w))
            g.DrawPath(pen, p);
    }
}

// 自绘按钮: 圆角 / 悬停 / 按下 / 选中态, 强调色跟随背光
internal class UiButton : Control
{
    private bool _hover, _down;
    private Color _accent = UiTheme.Accent(Lcd1602Control.BacklightKind.Blue);

    public bool Active;
    public bool Primary;          // 主按钮(实心强调色)

    public UiButton(string text, int w, int h)
    {
        Text = text;
        Size = new Size(w, h);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = UiTheme.FontUi;
    }

    public void SetAccent(Color c) { _accent = c; Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

        Color fill;
        Color border;
        Color fg = UiTheme.Text;
        if (Primary)
        {
            fill = _down ? UiTheme.Mix(_accent, Color.Black, 0.75f)
                 : _hover ? UiTheme.Mix(_accent, Color.White, 0.85f) : _accent;
            border = fill;
            fg = Color.FromArgb(18, 20, 24);
        }
        else if (Active)
        {
            fill = UiTheme.Mix(_accent, UiTheme.Surface, 0.30f);
            border = UiTheme.Mix(_accent, UiTheme.Surface, 0.55f);
        }
        else
        {
            fill = _down ? UiTheme.Surface : (_hover ? UiTheme.SurfaceHover : UiTheme.SurfaceAlt);
            border = _hover ? UiTheme.BorderFocus : UiTheme.Border;
        }

        UiTheme.FillRounded(g, r, 8, fill);
        UiTheme.StrokeRounded(g, r, 8, border, 1f);

        TextRenderer.DrawText(g, Text, Font, r, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

// 圆角输入容器: 内含一个无边框 TextBox, 聚焦时边框高亮
internal class UiField : Panel
{
    public readonly TextBox Box = new TextBox();
    private bool _focus;

    public UiField()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = UiTheme.Surface;
        Box.BorderStyle = BorderStyle.None;
        Box.BackColor = UiTheme.SurfaceAlt;
        Box.ForeColor = UiTheme.Text;
        Box.Font = UiTheme.FontMono;
        Box.GotFocus += (s, e) => { _focus = true; Invalidate(); };
        Box.LostFocus += (s, e) => { _focus = false; Invalidate(); };
        Controls.Add(Box);
    }

    public void SetAccent(Color c) { Invalidate(); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Box.SetBounds(12, (Height - Box.PreferredHeight) / 2 + 1, Math.Max(10, Width - 24), Box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
        UiTheme.FillRounded(g, r, 8, UiTheme.SurfaceAlt);
        UiTheme.StrokeRounded(g, r, 8, _focus ? Color.FromArgb(150, 111, 180, 255) : UiTheme.Border, _focus ? 1.6f : 1f);
    }
}
