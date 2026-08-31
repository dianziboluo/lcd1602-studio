using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// 1602 液晶模拟控件: 自绘 5x7 点阵, 蓝/绿/灭 三态背光, 支持超长行滚动
internal class Lcd1602Control : Control
{
    public enum BacklightKind { Blue = 0, Green = 1, Off = 2 }

    private string[] _lines = new string[2] { "", "" };
    private int[] _offs = new int[2];
    private bool[] _scroll = new bool[2];
    private BacklightKind _backlight = BacklightKind.Blue;
    private Timer _anim;
    private int _dropCol = -1, _dropRow = -1;

    public const int GridCols = 16, GridRows = 2;

    /// <summary>字形拖放到某格: (slot, col, row)</summary>
    public event Action<int, int, int> GlyphDropped;

    public Lcd1602Control()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(22, 23, 27);
        _anim = new Timer();
        _anim.Interval = 220;
        _anim.Tick += OnAnim;
        _anim.Start();

        AllowDrop = true;
        DragEnter += (s, e) =>
        {
            if (e.Data.GetDataPresent("LcdGlyph")) e.Effect = DragDropEffects.Copy;
            else e.Effect = DragDropEffects.None;
        };
        DragOver += (s, e) =>
        {
            if (e.Data.GetDataPresent("LcdGlyph"))
            {
                HitCell(e.X, e.Y, out _dropCol, out _dropRow);
                Invalidate();
                e.Effect = DragDropEffects.Copy;
            }
        };
        DragLeave += (s, e) => { _dropCol = -1; _dropRow = -1; Invalidate(); };
        DragDrop += (s, e) =>
        {
            if (e.Data.GetDataPresent("LcdGlyph"))
            {
                int slot = (int)e.Data.GetData("LcdGlyph");
                int col, row;
                HitCell(e.X, e.Y, out col, out row);
                _dropCol = -1; _dropRow = -1; Invalidate();
                if (GlyphDropped != null) GlyphDropped(slot, col, row);
            }
        };
    }

    private void HitCell(int x, int y, out int col, out int row)
    {
        col = -1; row = -1;
        int px = PixelSize;
        int gridW = GridCols * 6 * px, gridH = GridRows * 8 * px;
        int x0 = (ClientSize.Width - gridW) / 2;
        int y0 = (ClientSize.Height - gridH) / 2 - 4;
        col = (x - x0) / (6 * px);
        row = (y - y0) / (8 * px);
        if (col < 0 || col >= GridCols) col = -1;
        if (row < 0 || row >= GridRows) row = -1;
    }

    public BacklightKind Backlight
    {
        get { return _backlight; }
        set { _backlight = value; Invalidate(); }
    }

    public bool ScrollRow0 { get { return _scroll[0]; } set { _scroll[0] = value; _offs[0] = 0; Invalidate(); } }
    public bool ScrollRow1 { get { return _scroll[1]; } set { _scroll[1] = value; _offs[1] = 0; Invalidate(); } }

    public void SetText(string l0, string l1)
    {
        if (l0 == null) l0 = "";
        if (l1 == null) l1 = "";
        if (_lines[0] == l0 && _lines[1] == l1) return;
        _lines[0] = l0;
        _lines[1] = l1;
        // 注意: 不重置滚动偏移, 否则每秒数值变化会让走马灯反复回起点
        Invalidate();
    }

    public string RowText(int i) { return _lines[i]; }

    public int PixelSize
    {
        get
        {
            int w = ClientSize.Width - 40;
            int px = w / (GridCols * 6);
            if (px < 2) px = 2;
            if (px > 9) px = 9;
            return px;
        }
    }

    private void OnAnim(object sender, EventArgs e)
    {
        bool dirty = false;
        for (int r = 0; r < 2; r++)
        {
            if (_scroll[r] && _lines[r].Length > GridCols)
            {
                _offs[r]++;
                // 走到末尾后再滚一段"空白", 让走马灯像真机一样有间隔
                if (_offs[r] > _lines[r].Length) _offs[r] = 0;
                dirty = true;
            }
        }
        if (dirty) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        int px = PixelSize;
        int gridW = GridCols * 6 * px;
        int gridH = GridRows * 8 * px;
        int x0 = (ClientSize.Width - gridW) / 2;
        int y0 = (ClientSize.Height - gridH) / 2 - 4;

        // 外框
        Rectangle bezel = new Rectangle(x0 - 14, y0 - 12, gridW + 28, gridH + 30);
        using (GraphicsPath p = Rounded(bezel, 10))
        {
            using (SolidBrush b = new SolidBrush(Color.FromArgb(14, 15, 18)))
                g.FillPath(b, p);
            using (Pen pen = new Pen(Color.FromArgb(72, 78, 86)))
                g.DrawPath(pen, p);
        }

        // 玻璃底(背光)
        Palette pal = GetPalette();
        Color baseDark = pal.BaseDark, baseLight = pal.BaseLight,
              lit = pal.Lit, glow = pal.Glow, faint = pal.Faint;
        Rectangle glass = new Rectangle(x0 - 4, y0 - 2, gridW + 8, gridH + 10);
        Rectangle sq = new Rectangle(glass.X, glass.Y, glass.Width, glass.Height);
        using (GraphicsPath p = Rounded(sq, 5))
        {
            using (LinearGradientBrush b = new LinearGradientBrush(sq, baseLight, baseDark, 90f))
                g.FillPath(b, p);
            using (Pen pen = new Pen(Color.FromArgb(110, glow)))
                g.DrawPath(pen, p);
        }

        // 网格
        for (int r = 0; r < GridRows; r++)
        {
            string line = _lines[r];
            int off = _scroll[r] && line.Length > GridCols ? _offs[r] : 0;
            for (int c = 0; c < GridCols; c++)
            {
                char ch = (off + c < line.Length) ? line[off + c] : ' ';
                byte[] gl = Font5x7.Glyph(ch);
                int cx = x0 + c * 6 * px;
                int cy = y0 + r * 8 * px;
                DrawGlyph(g, gl, cx, cy, px, lit, glow, faint);
            }
        }

        // 玻璃高光(左上一点斜光)
        using (SolidBrush b = new SolidBrush(Color.FromArgb(14, 255, 255, 255)))
        {
            Point[] tri = new Point[]
            {
                new Point(glass.X, glass.Y),
                new Point(glass.X + glass.Width / 2, glass.Y),
                new Point(glass.X, glass.Y + glass.Height / 2)
            };
            g.FillPolygon(b, tri);
        }

        // 拖放目标格高亮
        if (_dropCol >= 0 && _dropRow >= 0)
        {
            Rectangle cell = new Rectangle(x0 + _dropCol * 6 * px - 1, y0 + _dropRow * 8 * px - 1,
                6 * px + 2, 8 * px + 2);
            using (Pen p = new Pen(Color.FromArgb(220, glow), Math.Max(1, px / 3)))
                g.DrawRectangle(p, cell);
        }
    }

    private void DrawGlyph(Graphics g, byte[] gl, int cx, int cy, int px, Color lit, Color glow, Color faint)
    {
        for (int col = 0; col < Font5x7.CharW; col++)
        {
            byte bits = gl[col];
            for (int row = 0; row < Font5x7.CharH; row++)
            {
                Rectangle dot = new Rectangle(cx + col * px, cy + row * px, px, px);
                if ((bits & (1 << row)) != 0)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(70, glow)))
                        g.FillRectangle(b, dot.X - px / 3, dot.Y - px / 3, dot.Width + px * 2 / 3, dot.Height + px * 2 / 3);
                    using (SolidBrush b = new SolidBrush(lit))
                        g.FillRectangle(b, dot);
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(faint))
                        g.FillRectangle(b, dot);
                }
            }
        }
    }

    private struct Palette
    {
        public Color BaseDark, BaseLight, Lit, Glow, Faint;
    }

    private Palette GetPalette()
    {
        Palette p = new Palette();
        switch (_backlight)
        {
            case BacklightKind.Green:
                p.BaseDark = Color.FromArgb(10, 22, 12);
                p.BaseLight = Color.FromArgb(20, 42, 22);
                p.Lit = Color.FromArgb(214, 255, 196);
                p.Glow = Color.FromArgb(126, 205, 84);
                p.Faint = Color.FromArgb(34, 56, 34);
                break;
            case BacklightKind.Off:
                p.BaseDark = Color.FromArgb(8, 9, 10);
                p.BaseLight = Color.FromArgb(16, 17, 19);
                p.Lit = Color.FromArgb(120, 122, 126);
                p.Glow = Color.FromArgb(70, 72, 76);
                p.Faint = Color.FromArgb(24, 26, 28);
                break;
            default:
                p.BaseDark = Color.FromArgb(5, 17, 26);
                p.BaseLight = Color.FromArgb(10, 30, 44);
                p.Lit = Color.FromArgb(232, 247, 255);
                p.Glow = Color.FromArgb(120, 180, 255);
                p.Faint = Color.FromArgb(30, 46, 58);
                break;
        }
        return p;
    }

    private static GraphicsPath Rounded(Rectangle r, int rad)
    {
        GraphicsPath p = new GraphicsPath();
        p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
        p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
        p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
        p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
        p.CloseFigure();
        return p;
    }
}
