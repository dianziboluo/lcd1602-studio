using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// 1602 液晶模拟控件
//  · 自绘 5x7 点阵, 蓝/绿/灭三档背光
//  · 背景(外框/玻璃/渐变/暗像素)与每个字形都做位图缓存 -> 每秒刷新不再闪动
//  · 支持超长行走马灯、字形拖放定位
internal class Lcd1602Control : Control
{
    public enum BacklightKind { Blue = 0, Green = 1, Off = 2 }

    public const int GridCols = 16, GridRows = 2;

    private string[] _lines = new string[2] { "", "" };
    private int[] _offs = new int[2];
    private bool[] _scroll = new bool[2];
    private BacklightKind _backlight = BacklightKind.Blue;
    private Timer _anim;
    private int _dropCol = -1, _dropRow = -1;

    private int _px = 8;                       // 每个点阵像素占几个屏幕像素
    private Bitmap _bg;                        // 缓存的静态背景
    private int _bgPx = -1;
    private BacklightKind _bgKind = (BacklightKind)(-1);
    private readonly Dictionary<string, Bitmap> _tiles = new Dictionary<string, Bitmap>();

    /// <summary>字形拖放到某格: (slot, col, row)</summary>
    public event Action<int, int, int> GlyphDropped;

    public Lcd1602Control()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiTheme.Bg;
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

    public BacklightKind Backlight
    {
        get { return _backlight; }
        set { _backlight = value; _bg = null; _tiles.Clear(); Invalidate(); }
    }

    public bool ScrollRow0 { get { return _scroll[0]; } set { _scroll[0] = value; _offs[0] = 0; Invalidate(); } }
    public bool ScrollRow1 { get { return _scroll[1]; } set { _scroll[1] = value; _offs[1] = 0; Invalidate(); } }

    /// <summary>点阵像素大小(主窗体按可用宽度算好后设置)</summary>
    public int PixelSize
    {
        get { return _px; }
        set
        {
            int v = value < 4 ? 4 : (value > 13 ? 13 : value);
            if (v == _px) return;
            _px = v;
            _bg = null; _tiles.Clear();
            Invalidate();
        }
    }

    /// <summary>控件为点阵区域预留的外框尺寸</summary>
    public static Size FrameSize(int px)
    {
        return new Size(GridCols * 6 * px + 56, GridRows * 8 * px + 64);
    }

    public void SetText(string l0, string l1)
    {
        if (l0 == null) l0 = "";
        if (l1 == null) l1 = "";
        if (_lines[0] == l0 && _lines[1] == l1) return;
        _lines[0] = l0;
        _lines[1] = l1;
        Invalidate();   // 不重置滚动偏移, 否则每秒数值变化会让走马灯反复回起点
    }

    public string RowText(int i) { return _lines[i]; }

    private void OnAnim(object sender, EventArgs e)
    {
        bool dirty = false;
        for (int r = 0; r < 2; r++)
        {
            if (_scroll[r] && _lines[r].Length > GridCols)
            {
                _offs[r]++;
                if (_offs[r] > _lines[r].Length) _offs[r] = 0;
                dirty = true;
            }
        }
        if (dirty) Invalidate();
    }

    private void HitCell(int x, int y, out int col, out int row)
    {
        col = -1; row = -1;
        int gx, gy;
        GridOrigin(out gx, out gy);
        col = (x - gx) / (6 * _px);
        row = (y - gy) / (8 * _px);
        if (col < 0 || col >= GridCols) col = -1;
        if (row < 0 || row >= GridRows) row = -1;
    }

    private void GridOrigin(out int gx, out int gy)
    {
        int gridW = GridCols * 6 * _px, gridH = GridRows * 8 * _px;
        gx = (ClientSize.Width - gridW) / 2;
        gy = (ClientSize.Height - gridH) / 2;
    }

    // ---------- 绘制 ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        EnsureBackground();
        if (_bg != null) g.DrawImageUnscaled(_bg, 0, 0);

        int gx, gy;
        GridOrigin(out gx, out gy);

        for (int r = 0; r < GridRows; r++)
        {
            string line = _lines[r];
            int off = _scroll[r] && line.Length > GridCols ? _offs[r] : 0;
            for (int c = 0; c < GridCols; c++)
            {
                char ch = (off + c < line.Length) ? line[off + c] : ' ';
                if (ch == ' ') continue;
                Bitmap tile = Tile(ch);
                if (tile != null) g.DrawImageUnscaled(tile, gx + c * 6 * _px, gy + r * 8 * _px);
            }
        }

        if (_dropCol >= 0 && _dropRow >= 0)
        {
            Rectangle cell = new Rectangle(gx + _dropCol * 6 * _px - 2, gy + _dropRow * 8 * _px - 2,
                                           6 * _px + 4, 8 * _px + 4);
            UiTheme.StrokeRounded(g, cell, 3, UiTheme.Accent(_backlight), 2f);
        }
    }

    private void EnsureBackground()
    {
        if (_bg != null && _bgPx == _px && _bgKind == _backlight
            && _bg.Width == ClientSize.Width && _bg.Height == ClientSize.Height) return;
        if (ClientSize.Width < 20 || ClientSize.Height < 20) return;

        _bg = new Bitmap(ClientSize.Width, ClientSize.Height);
        using (Graphics g = Graphics.FromImage(_bg))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(UiTheme.Bg);

            int gridW = GridCols * 6 * _px, gridH = GridRows * 8 * _px;
            int gx = (ClientSize.Width - gridW) / 2, gy = (ClientSize.Height - gridH) / 2;

            // 外框 + 柔和投影
            Rectangle frame = new Rectangle(gx - 20, gy - 20, gridW + 40, gridH + 40);
            for (int i = 3; i >= 1; i--)
            {
                Rectangle sh = new Rectangle(frame.X - i * 2, frame.Y - i * 2 + 3,
                                             frame.Width + i * 4, frame.Height + i * 4);
                UiTheme.FillRounded(g, sh, 16 + i * 2, Color.FromArgb(10, 0, 0, 0));
            }
            UiTheme.FillRounded(g, frame, 16, Color.FromArgb(14, 15, 18));
            UiTheme.StrokeRounded(g, frame, 16, UiTheme.Border, 1.4f);

            // 玻璃(背光)
            Color baseDark, baseLight, glow, faint;
            Palette(out baseDark, out baseLight, out glow, out faint);
            Rectangle glass = new Rectangle(gx - 8, gy - 8, gridW + 16, gridH + 16);
            using (GraphicsPath p = UiTheme.Rounded(glass, 8))
            {
                using (LinearGradientBrush b = new LinearGradientBrush(glass, baseLight, baseDark, 90f))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(120, glow), 1.2f))
                    g.DrawPath(pen, p);
            }

            // 未点亮的暗像素(静态)
            using (SolidBrush fb = new SolidBrush(faint))
            {
                for (int r = 0; r < GridRows; r++)
                    for (int c = 0; c < GridCols; c++)
                        for (int col = 0; col < Font5x7.CharW; col++)
                            for (int row = 0; row < Font5x7.CharH; row++)
                                g.FillRectangle(fb, gx + c * 6 * _px + col * _px,
                                                gy + r * 8 * _px + row * _px, _px, _px);
            }

            // 玻璃高光
            using (SolidBrush hb = new SolidBrush(Color.FromArgb(16, 255, 255, 255)))
            {
                Point[] tri = new Point[]
                {
                    new Point(glass.X, glass.Y),
                    new Point(glass.X + glass.Width / 2, glass.Y),
                    new Point(glass.X, glass.Y + glass.Height / 2)
                };
                g.FillPolygon(hb, tri);
            }
        }
        _bgPx = _px;
        _bgKind = _backlight;
    }

    /// <summary>字形位图缓存(点亮像素 + 光晕; 其余透明, 让背景暗像素透出)</summary>
    private Bitmap Tile(char ch)
    {
        string key = ((int)ch) + "|" + _px + "|" + (int)_backlight;
        Bitmap bmp;
        if (_tiles.TryGetValue(key, out bmp)) return bmp;

        byte[] gl = Font5x7.Glyph(ch);
        Color lit, glow;
        switch (_backlight)
        {
            case BacklightKind.Green:
                lit = Color.FromArgb(214, 255, 196); glow = Color.FromArgb(126, 205, 84); break;
            case BacklightKind.Off:
                lit = Color.FromArgb(120, 122, 126); glow = Color.FromArgb(70, 72, 76); break;
            default:
                lit = Color.FromArgb(232, 247, 255); glow = Color.FromArgb(120, 180, 255); break;
        }

        bmp = new Bitmap(6 * _px, 8 * _px, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.None;
            int pad = Math.Max(1, _px / 3);
            for (int col = 0; col < Font5x7.CharW; col++)
            {
                byte bits = gl[col];
                for (int row = 0; row < Font5x7.CharH; row++)
                {
                    if ((bits & (1 << row)) == 0) continue;
                    int x = col * _px, y = row * _px;
                    using (SolidBrush gb = new SolidBrush(Color.FromArgb(64, glow)))
                        g.FillRectangle(gb, x - pad, y - pad, _px + pad * 2, _px + pad * 2);
                    using (SolidBrush lb = new SolidBrush(lit))
                        g.FillRectangle(lb, x, y, _px, _px);
                }
            }
        }
        _tiles[key] = bmp;
        return bmp;
    }

    private void Palette(out Color baseDark, out Color baseLight, out Color glow, out Color faint)
    {
        switch (_backlight)
        {
            case BacklightKind.Green:
                baseDark = Color.FromArgb(10, 24, 13);
                baseLight = Color.FromArgb(24, 48, 25);
                glow = Color.FromArgb(126, 205, 84);
                faint = Color.FromArgb(36, 60, 36);
                break;
            case BacklightKind.Off:
                baseDark = Color.FromArgb(8, 9, 10);
                baseLight = Color.FromArgb(18, 19, 22);
                glow = Color.FromArgb(70, 72, 76);
                faint = Color.FromArgb(26, 28, 30);
                break;
            default:
                baseDark = Color.FromArgb(6, 20, 32);
                baseLight = Color.FromArgb(12, 36, 54);
                glow = Color.FromArgb(120, 180, 255);
                faint = Color.FromArgb(30, 48, 62);
                break;
        }
    }
}
