using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// 数据台公共部分: 变量项模型 + 自绘行渲染 + 命令面板(搜索即插入)

internal enum BadgeKind { Inner, Temp, Fan, Usage, Glyph }

internal class VarItem
{
    public const int CatBuiltin = 0, CatHw = 1;
    public int Cat;
    public BadgeKind Badge;
    public string Name;          // 显示名, 如 "CPU 占用"
    public string Token;         // 插入的 token, 如 "{cpu}" / "h|CPU|CPU Fan"
    public string Unit;          // 单位列, 如 "%" / "RPM" / ""
    public Func<DataEngine, string> ValueFn;   // 实时值(每次重绘取)

    // 组头行
    public bool IsGroup;
    public int GroupIndex;
    public int Count;
    public bool Collapsed;

    // 自定义字形项
    public int SlotIdx = -1;

    public static VarItem Glyph(int slot)
    {
        VarItem v = new VarItem();
        v.Cat = CatBuiltin;
        v.Badge = BadgeKind.Glyph;
        v.Name = "字形 " + (slot + 1) + " · 拖到屏上";
        v.Token = "g" + (slot + 1);
        v.Unit = "";
        v.SlotIdx = slot;
        v.ValueFn = eng => "●";
        return v;
    }

    public static VarItem Builtin(string name, string token, string unit, Func<DataEngine, string> fn)
    {
        VarItem v = new VarItem();
        v.Cat = CatBuiltin; v.Badge = BadgeKind.Inner; v.Name = name; v.Token = token; v.Unit = unit; v.ValueFn = fn;
        return v;
    }

    public static VarItem Hw(HwEntry e)
    {
        VarItem v = new VarItem();
        v.Cat = CatHw;
        v.Badge = e.Type == HwEntry.TypeFan ? BadgeKind.Fan
               : e.Type == HwEntry.TypeUsage ? BadgeKind.Usage : BadgeKind.Temp;
        v.Name = e.Sensor + " · " + e.Reading;
        v.Token = e.Key();
        v.Unit = e.Unit ?? "";
        string key = e.Key();
        v.ValueFn = eng => { string s = eng.FindHw(key); return s ?? "--"; };
        return v;
    }
}

internal static class UiFonts
{
    public static readonly Font Name = new Font("Microsoft YaHei UI", 9f);
    public static readonly Font Value = new Font("Consolas", 9f);
}

internal static class VarPainter
{
    public static Color Accent(Lcd1602Control.BacklightKind k)
    {
        return UiTheme.Accent(k);
    }

    public static Color BadgeBack(BadgeKind b)
    {
        if (b == BadgeKind.Temp) return Color.FromArgb(96, 66, 30);
        if (b == BadgeKind.Fan) return Color.FromArgb(28, 78, 96);
        if (b == BadgeKind.Usage) return Color.FromArgb(78, 56, 110);
        return Color.FromArgb(56, 64, 76);
    }

    public static Color BadgeText(BadgeKind b)
    {
        if (b == BadgeKind.Temp) return Color.FromArgb(255, 214, 160);
        if (b == BadgeKind.Fan) return Color.FromArgb(178, 234, 250);
        if (b == BadgeKind.Usage) return Color.FromArgb(218, 198, 255);
        return Color.FromArgb(206, 214, 226);
    }

    public static string BadgeLabel(BadgeKind b)
    {
        if (b == BadgeKind.Temp) return "温";
        if (b == BadgeKind.Fan) return "扇";
        if (b == BadgeKind.Usage) return "占";
        if (b == BadgeKind.Glyph) return "字";
        return "内";
    }

    /// <summary>绘制一个数据行: 徽章 | 名字 | (右对齐)实时值 单位</summary>
    public static void DrawRow(Graphics g, Rectangle r, VarItem item, DataEngine eng,
                               Color accent, bool selected, bool hovered, Font nameFont, Font valFont)
    {
        if (selected) g.FillRectangle(new SolidBrush(Color.FromArgb(44, accent)), r);
        else if (hovered) g.FillRectangle(new SolidBrush(Color.FromArgb(24, 255, 255, 255)), r);

        int y = r.Y + (r.Height - 18) / 2;
        int nameX = r.X + 40;
        if (item.SlotIdx >= 0)
        {
            // 自定义字形: 画 5x7 微缩图代替徽章
            byte[] col = CustomGlyphs.ToColumns(item.SlotIdx);
            for (int c = 0; c < 5; c++)
                for (int rr = 0; rr < 7; rr++)
                    if ((col[c] & (1 << rr)) != 0)
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 210, 220)))
                            g.FillRectangle(b, r.X + 8 + c * 2, r.Y + 8 + rr * 2, 2, 2);
                    }
            using (Pen p = new Pen(Color.FromArgb(80, 90, 100)))
                g.DrawRectangle(p, r.X + 6, r.Y + 6, 14, 18);
            nameX = r.X + 28;
        }
        else
        {
            Rectangle badge = new Rectangle(r.X + 6, y, 26, 18);
            using (GraphicsPath p = Rounded(badge, 4))
            {
                g.FillPath(new SolidBrush(BadgeBack(item.Badge)), p);
            }
            g.DrawString(BadgeLabel(item.Badge), nameFont, new SolidBrush(BadgeText(item.Badge)), badge.X, badge.Y - 1);
        }

        string val = item.ValueFn != null ? item.ValueFn(eng) : "--";
        bool missing = val == null || val == "--" || val.StartsWith("--");
        string unit = item.Unit == null ? "" : item.Unit.Trim();
        SizeF vs = g.MeasureString(val, valFont);
        float vx = r.Right - 12 - vs.Width - (unit.Length > 0 ? g.MeasureString(unit, valFont).Width + 6 : 0);
        g.DrawString(val, valFont, new SolidBrush(missing ? UiTheme.TextFaint : UiTheme.Text), vx, r.Y + (r.Height - vs.Height) / 2 + 1);
        if (unit.Length > 0)
            g.DrawString(unit, valFont, new SolidBrush(UiTheme.TextDim), r.Right - 12 - g.MeasureString(unit, valFont).Width, r.Y + (r.Height - vs.Height) / 2 + 1);

        g.DrawString(item.Name, nameFont, new SolidBrush(UiTheme.Text), nameX, r.Y + (r.Height - nameFont.Height) / 2 + 1);
    }

    /// <summary>绘制分组头: ▸/▾ 箭头 + 标题 + 右侧项数</summary>
    public static void DrawGroupRow(Graphics g, Rectangle r, VarItem grp, Font nameFont)
    {
        g.FillRectangle(new SolidBrush(UiTheme.SurfaceAlt), r);
        g.DrawString(grp.Collapsed ? "▸" : "▾", nameFont, new SolidBrush(Accent3(grp.GroupIndex)), r.X + 8, r.Y + (r.Height - nameFont.Height) / 2 + 1);
        g.DrawString(grp.Name, nameFont, new SolidBrush(UiTheme.TextDim), r.X + 26, r.Y + (r.Height - nameFont.Height) / 2 + 1);
        string count = grp.Count.ToString();
        SizeF cs = g.MeasureString(count, nameFont);
        g.DrawString(count, nameFont, new SolidBrush(UiTheme.TextFaint), r.Right - 12 - cs.Width, r.Y + (r.Height - cs.Height) / 2 + 1);
    }

    private static Color Accent3(int groupIndex)
    {
        if (groupIndex == 1) return Color.FromArgb(255, 176, 96);
        if (groupIndex == 2) return Color.FromArgb(96, 200, 230);
        if (groupIndex == 3) return Color.FromArgb(180, 140, 240);
        return Color.FromArgb(150, 160, 172);
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

// 命令面板: 搜索即插入(Enter 插入, Esc 关闭, ↑↓ 选择)
internal class CommandPaletteForm : Form
{
    private readonly List<VarItem> _items;
    private readonly DataEngine _eng;
    private readonly Action<string> _onPick;
    private TextBox _search;
    private ListBox _list;
    private int _hover = -1;

    public CommandPaletteForm(List<VarItem> items, DataEngine eng, Action<string> onPick)
    {
        _items = items;
        _eng = eng;
        _onPick = onPick;

        Text = "插入变量";
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(28, 29, 34);
        ClientSize = new Size(500, 360);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); } };

        _search = new TextBox();
        _search.Dock = DockStyle.Top;
        _search.Height = 34;
        _search.Font = new Font("Microsoft YaHei UI", 10.5f);
        _search.BackColor = Color.FromArgb(38, 40, 46);
        _search.ForeColor = Color.FromArgb(235, 238, 242);
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.TextChanged += (s, e) => RefreshList(_search.Text);
        Controls.Add(_search);

        Label hintBar = new Label();
        hintBar.Text = "Enter 插入   Esc 关闭   ↑↓ 选择";
        hintBar.Dock = DockStyle.Bottom;
        hintBar.Height = 24;
        hintBar.ForeColor = Color.FromArgb(120, 126, 134);
        hintBar.Font = new Font("Microsoft YaHei UI", 8f);
        hintBar.TextAlign = ContentAlignment.MiddleRight;
        hintBar.BackColor = Color.FromArgb(20, 21, 24);
        Controls.Add(hintBar);

        _list = new ListBox();
        _list.Dock = DockStyle.Fill;
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.ItemHeight = 30;
        _list.BackColor = Color.FromArgb(28, 29, 34);
        _list.BorderStyle = BorderStyle.None;
        _list.Font = new Font("Microsoft YaHei UI", 9f);
        _list.DrawItem += DrawItem;
        _list.MouseMove += (s, e) =>
        {
            int i = _list.IndexFromPoint(e.Location);
            if (i != _hover) { _hover = i; _list.Invalidate(); }
        };
        _list.SelectedIndexChanged += (s, e) =>
        {
            VarItem v = _list.SelectedItem as VarItem;
            if (v != null) { _onPick(v.Token); Close(); }
        };
        Controls.Add(_list);

        RefreshList("");
    }

    private void RefreshList(string q)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        q = q == null ? "" : q.Trim().ToLowerInvariant();
        foreach (VarItem v in _items)
        {
            if (q.Length > 0 && !v.Name.ToLowerInvariant().Contains(q)
                && !v.Token.ToLowerInvariant().Contains(q)) continue;
            _list.Items.Add(v);
        }
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void DrawItem(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count) return;
        VarItem v = _list.Items[e.Index] as VarItem;
        if (v == null) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        Color accent = VarPainter.Accent(_eng == null ? Lcd1602Control.BacklightKind.Blue
            : CurrentBacklight);
        VarPainter.DrawRow(g, e.Bounds, v, _eng, accent, sel, e.Index == _hover,
            UiFonts.Name, UiFonts.Value);
    }

    private Lcd1602Control.BacklightKind CurrentBacklight
    {
        get { return (Owner as MainForm) != null ? ((MainForm)Owner).CurrentBacklight : Lcd1602Control.BacklightKind.Blue; }
    }
}
