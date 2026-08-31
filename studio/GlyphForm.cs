using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// 点阵字形编辑器: 手画 5x7 自定义字符(8 槽), 与真屏 CGRAM 同步
internal class GlyphForm : Form
{
    private readonly Action<int> _onChanged;
    private GlyphGrid _grid;
    private GlyphPreview _preview;
    private ListBox _slots;
    private Label _info;
    private int _cur;

    public GlyphForm(int slot, Action<int> onChanged)
    {
        _onChanged = onChanged;
        _cur = slot < 0 || slot > 7 ? 1 : slot;

        Text = "点阵字形编辑器 — 手画 5x7 自定义字符";
        ClientSize = new Size(640, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(27, 28, 32);
        ForeColor = Color.FromArgb(224, 226, 230);
        Font = new Font("Microsoft YaHei UI", 9f);

        Label t = new Label();
        t.Text = "选择槽位,在左侧网格上按住鼠标绘制(左键点亮 / 右键擦除)。";
        t.Location = new Point(14, 12);
        t.Size = new Size(420, 20);
        t.ForeColor = Color.FromArgb(150, 153, 160);
        Controls.Add(t);

        _slots = new ListBox();
        _slots.Location = new Point(14, 40);
        _slots.Size = new Size(96, 300);
        _slots.DrawMode = DrawMode.OwnerDrawFixed;
        _slots.ItemHeight = 40;
        _slots.BackColor = Color.FromArgb(30, 31, 36);
        _slots.ForeColor = Color.FromArgb(220, 222, 226);
        _slots.BorderStyle = BorderStyle.None;
        for (int i = 0; i < 8; i++) _slots.Items.Add(i);
        _slots.DrawItem += (s, e) =>
        {
            int idx = (int)_slots.Items[e.Index];
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if ((e.State & DrawItemState.Selected) != 0)
                g.FillRectangle(new SolidBrush(Color.FromArgb(44, 111, 180, 255)), e.Bounds);
            byte[] col = CustomGlyphs.ToColumns(idx);
            for (int c = 0; c < 5; c++)
                for (int r = 0; r < 7; r++)
                    if ((col[c] & (1 << r)) != 0)
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 230, 238)))
                            g.FillRectangle(b, e.Bounds.X + 8 + c * 3, e.Bounds.Y + 8 + r * 3, 3, 3);
                    }
            using (Pen p = new Pen(Color.FromArgb(80, 90, 100)))
                g.DrawRectangle(p, e.Bounds.X + 6, e.Bounds.Y + 6, 21, 25);
            g.DrawString("槽" + (idx + 1), Font, new SolidBrush(Color.FromArgb(200, 208, 216)),
                e.Bounds.X + 34, e.Bounds.Y + 11);
        };
        _slots.SelectedIndexChanged += (s, e) => { _cur = _slots.SelectedIndex; RefreshAll(); };
        _slots.MouseDown += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            int i = _slots.IndexFromPoint(e.Location);
            if (i < 0) return;
            DataObject d = new DataObject();
            d.SetData("LcdGlyph", i);
            _slots.DoDragDrop(d, DragDropEffects.Copy);
        };
        Controls.Add(_slots);

        _grid = new GlyphGrid();
        _grid.Location = new Point(124, 40);
        _grid.Size = new Size(5 * 28 + 8, 7 * 28 + 8);
        _grid.Changed += OnGridChanged;
        Controls.Add(_grid);

        _preview = new GlyphPreview();
        _preview.Location = new Point(300, 40);
        _preview.Size = new Size(180, 160);
        Controls.Add(_preview);

        _info = new Label();
        _info.Location = new Point(300, 210);
        _info.Size = new Size(320, 40);
        _info.ForeColor = Color.FromArgb(150, 153, 160);
        Controls.Add(_info);

        Button bClear = FlatBtn("清空");
        bClear.Location = new Point(300, 258);
        bClear.Click += (s, e) => { ZeroSlot(); RefreshAll(); Notify(); };
        Controls.Add(bClear);

        Button bDef = FlatBtn("恢复默认 ℃");
        bDef.Location = new Point(300, 292);
        bDef.Click += (s, e) =>
        {
            if (_cur == 1) CustomGlyphs.Slot[1] = new byte[] { 0x0E, 0x0A, 0x0E, 0x0C, 0x02, 0x02, 0x0C, 0x00 };
            else ZeroSlot();
            RefreshAll(); Notify();
        };
        Controls.Add(bDef);

        Button bIns = FlatBtn("插入 {g" + (_cur + 1) + "}");
        bIns.Click += (s, e) => { InsertToken("g" + (_cur + 1)); };
        Controls.Add(bIns);
        _slots.SelectedIndexChanged += (s, e) => bIns.Text = "插入 {g" + (_cur + 1) + "}";

        Button bOk = FlatBtn("完成");
        bOk.Location = new Point(540, 330);
        bOk.Click += (s, e) => Close();
        Controls.Add(bOk);

        RefreshAll();
        _slots.SelectedIndex = _cur;
    }

    private void ZeroSlot() { for (int i = 0; i < 8; i++) CustomGlyphs.Slot[_cur][i] = 0; }

    private void OnGridChanged() { Notify(); RefreshPreview(); }

    private void Notify()
    {
        if (_onChanged != null) _onChanged(_cur);
    }

    private void RefreshAll()
    {
        _grid.SetGlyph(_cur);
        _grid.Invalidate();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        _preview.SetGlyph(_cur);
        _preview.Invalidate();
        _info.Text = "槽 " + (_cur + 1) + " · " + CustomGlyphs.ToHex(_cur) + "\r\n真屏 CGRAM 槽 " + _cur + ", 用 {g" + (_cur + 1) + "} 插入模板";
    }

    private static void InsertToken(string token)
    {
        Action<string> ins = MainForm.CurrentRowInserter;
        if (ins != null) ins(token);
    }

    private static Button FlatBtn(string text)
    {
        Button b = new Button();
        b.Text = text;
        b.Size = new Size(120, 28);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(90, 96, 106);
        b.BackColor = Color.FromArgb(58, 60, 66);
        b.ForeColor = Color.FromArgb(230, 232, 236);
        return b;
    }
}

// 5x7 手绘网格(左键点亮, 右键擦除, 拖动连续绘制)
internal class GlyphGrid : Control
{
    public event Action Changed;
    private int _slot;
    private int _drawMode;   // 1 点亮 0 擦除 -1 无
    private Point _last = new Point(-1, -1);
    private const int Cell = 28;

    public GlyphGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.FromArgb(22, 23, 27);
        Cursor = Cursors.Cross;
    }

    public void SetGlyph(int slot) { _slot = slot; }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        for (int c = 0; c < 5; c++)
            for (int r = 0; r < 7; r++)
            {
                bool on = (CustomGlyphs.Slot[_slot][r] & (1 << c)) != 0;
                Rectangle cell = new Rectangle(4 + c * Cell, 4 + r * Cell, Cell - 3, Cell - 3);
                if (on)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(232, 247, 255)))
                        g.FillRectangle(b, cell);
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 44, 50)))
                        g.FillRectangle(b, cell);
                }
            }
        using (Pen p = new Pen(Color.FromArgb(60, 66, 74)))
            g.DrawRectangle(p, 3, 3, 5 * Cell + 1, 7 * Cell + 1);
    }

    private Point CellFrom(MouseEventArgs e)
    {
        int c = (e.X - 4) / Cell, r = (e.Y - 4) / Cell;
        if (c < 0 || c > 4 || r < 0 || r > 6) return new Point(-1, -1);
        return new Point(c, r);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _drawMode = e.Button == MouseButtons.Left ? 1 : 0;
        PaintCells(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drawMode >= 0) PaintCells(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _drawMode = -1; }

    private void PaintCells(Point loc)
    {
        Point cell = CellFrom(new MouseEventArgs(MouseButtons.None, 0, loc.X, loc.Y, 0));
        if (cell.X < 0 || cell == _last) return;
        _last = cell;
        byte b = CustomGlyphs.Slot[_slot][cell.Y];
        if (_drawMode == 1) b |= (byte)(1 << cell.X);
        else b &= (byte)~(1 << cell.X);
        CustomGlyphs.Slot[_slot][cell.Y] = b;
        Invalidate();
        if (Changed != null) Changed();
    }
}

// 预览: LCD 风格大格显示当前字形
internal class GlyphPreview : Control
{
    private int _slot;

    public GlyphPreview()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.FromArgb(22, 23, 27);
    }

    public void SetGlyph(int slot) { _slot = slot; }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Color.FromArgb(28, 29, 34));
        byte[] col = CustomGlyphs.ToColumns(_slot);
        for (int c = 0; c < 5; c++)
            for (int r = 0; r < 7; r++)
            {
                if ((col[c] & (1 << r)) == 0) continue;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(120, 180, 255)))
                    g.FillRectangle(b, 40 + c * 20, 20 + r * 20, 17, 17);
            }
        using (Pen p = new Pen(Color.FromArgb(80, 90, 100)))
            g.DrawRectangle(p, 38, 18, 5 * 20 + 2, 7 * 20 + 2);
    }
}
