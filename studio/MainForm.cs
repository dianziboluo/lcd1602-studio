using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

// LCD1602 Studio
// 桌面端 1602 模拟器: 模板自由文本 + 变量插槽; 数据源 LHM(内置) + HWiNFO(可选)
// 用法: LCD1602Studio.exe [--selftest]

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length > 0 && args[0] == "--selftest")
        {
            Environment.Exit(MainForm.RunSelfTest());
            return;
        }
        Application.Run(new MainForm(false));
    }
}

internal class MainForm : Form
{
    private DataEngine _eng = new DataEngine();
    private SerialSync _sync = new SerialSync();
    private Lcd1602Control _lcd;
    private Panel _host, _editor, _varHost, _statusBar;
    private TextBox _tb0, _tb1, _search;
    private CheckBox _chk0, _chk1;
    private ListBox _vars;
    private Label _lhmStatus, _hwStatus, _footer;
    private Button _btnBack, _tabAll, _tabBuiltin, _tabHw, _btnCollapse, _btnSave, _btnSync, _btnPreset, _btnGlyph;
    private Panel _tabsHost;
    private Label _titleLabel;
    private Timer _timer;
    private NotifyIcon _tray;
    private bool _reallyClose, _trayHintShown;
    private int _focusedRow, _hoverVar = -1, _tabMode;   // 0 全部 1 内置 2 HWiNFO
    private int _saveFlashUntil;
    private readonly bool _selftest;
    private readonly List<VarItem> _varItems = new List<VarItem>();
    private readonly bool[] _grpCollapsed = new bool[5] { false, true, true, true, false };  // 常用/字形展开, HWiNFO 组折叠
    private bool _varsCollapsed;
    private static readonly string[] GrpTitles = { "常用", "HWiNFO · 温度", "HWiNFO · 风扇", "HWiNFO · 占用", "字形 · 拖到屏上" };

    private const int RowLabelW = 48, RowBoxX = 66, RowChkW = 64, RowBtnW = 92, RowH = 52;

    public MainForm(bool selftest)
    {
        _selftest = selftest;
        Text = "LCD1602 Studio — 桌上的一点光";
        ClientSize = new Size(980, 660);
        MinimumSize = new Size(860, 580);
        BackColor = Color.FromArgb(27, 28, 32);
        ForeColor = Color.FromArgb(224, 226, 230);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        BuildUI();
        LoadSettings();
        _eng.Open();
        UpdateStatus();
        RefreshVarList();
        ApplyVarCollapse();
        LayoutLcd();
        LayoutEditor();

        if (!_selftest)
        {
            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += OnTick;
            _timer.Start();
            OnTick(null, EventArgs.Empty);
        }

        FormClosing += (s, e) => SaveSettings();
        FormClosed += (s, e) =>
        {
            _eng.Dispose();
            _sync.Dispose();
            CurrentRowInserter = null;
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        };
        CurrentRowInserter = token => { InsertToken(CurrentBox(), "{" + token + "}"); Render(); };
        InitTray();
        Shown += (s, e) => { LayoutLcd(); LayoutEditor(); Render(); _search.Focus(); };
    }

    // 点 X → 缩到右下角托盘, 后台继续运行/同步; 托盘右键可退出
    private void InitTray()
    {
        _tray = new NotifyIcon();
        _tray.Icon = MakeTrayIcon();
        _tray.Text = "LCD1602 Studio — 桌上的一点光";
        ContextMenuStrip m = new ContextMenuStrip();
        ToolStripMenuItem show = new ToolStripMenuItem("显示主窗口");
        show.Click += (s, e) => RestoreFromTray();
        ToolStripMenuItem quit = new ToolStripMenuItem("退出");
        quit.Click += (s, e) => { _reallyClose = true; Close(); };
        m.Items.Add(show);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(quit);
        _tray.ContextMenuStrip = m;
        _tray.DoubleClick += (s, e) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        ShowInTaskbar = true;
        _tray.Visible = false;
    }

    private static Icon MakeTrayIcon()
    {
        Bitmap b = new Bitmap(16, 16);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.Clear(Color.Transparent);
            using (SolidBrush bd = new SolidBrush(Color.FromArgb(24, 25, 28)))
                g.FillRectangle(bd, 1, 3, 14, 10);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(120, 180, 255)))
                g.FillRectangle(bg, 2, 4, 12, 8);
            using (SolidBrush lit = new SolidBrush(Color.FromArgb(232, 247, 255)))
            {
                g.FillRectangle(lit, 4, 6, 3, 1);
                g.FillRectangle(lit, 8, 6, 4, 1);
                g.FillRectangle(lit, 4, 9, 3, 1);
                g.FillRectangle(lit, 8, 9, 4, 1);
            }
        }
        IntPtr h = b.GetHicon();
        Icon ic = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        b.Dispose();
        return ic;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            _tray.Visible = true;
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(2000, "LCD1602 Studio",
                    "已缩到右下角托盘,后台继续运行与同步。双击图标恢复窗口。", ToolTipIcon.Info);
            }
            return;
        }
        base.OnFormClosing(e);
    }

    public Lcd1602Control.BacklightKind CurrentBacklight { get { return _lcd.Backlight; } }
    public string GetEditorBounds() { return _editor.Bounds.ToString(); }
    public string GetHostBounds() { return _host.Bounds.ToString(); }
    public string GetVarsBounds() { return _varHost.Bounds.ToString(); }
    public string GetStatusBounds() { return _statusBar.Bounds.ToString(); }

    // ---------- UI ----------

    private void BuildUI()
    {
        _statusBar = new Panel();
        _statusBar.Dock = DockStyle.Bottom;
        _statusBar.Height = 38;
        _statusBar.BackColor = Color.FromArgb(20, 21, 24);

        _lhmStatus = NewStatusLabel();
        _lhmStatus.Location = new Point(12, 9);
        _statusBar.Controls.Add(_lhmStatus);
        _hwStatus = NewStatusLabel();
        _hwStatus.Location = new Point(400, 9);
        _hwStatus.Cursor = Cursors.Hand;
        _hwStatus.Click += (s, e) => OpenHwGuide();
        _statusBar.Controls.Add(_hwStatus);

        // ---- 数据台(子面板 Dock: 列表先加, 标题最后加) ----
        _varHost = new Panel();
        _varHost.Dock = DockStyle.Right;
        _varHost.Width = 312;
        _varHost.BackColor = Color.FromArgb(28, 29, 34);
        _varHost.Padding = new Padding(10, 8, 10, 4);
        Controls.Add(_varHost);

        _vars = new ListBox();
        _vars.Dock = DockStyle.Fill;
        _vars.DrawMode = DrawMode.OwnerDrawFixed;
        _vars.ItemHeight = 30;
        _vars.BackColor = Color.FromArgb(28, 29, 34);
        _vars.BorderStyle = BorderStyle.None;
        _vars.Font = new Font("Microsoft YaHei UI", 9f);
        _vars.DrawItem += DrawVarRow;
        _vars.MouseMove += (s, e) =>
        {
            int i = _vars.IndexFromPoint(e.Location);
            if (i != _hoverVar) { _hoverVar = i; _vars.Invalidate(); }
        };
        _vars.SelectedIndexChanged += OnVarPicked;
        _vars.MouseDown += (s, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            int i = _vars.IndexFromPoint(e.Location);
            if (i < 0) return;
            VarItem v = _vars.Items[i] as VarItem;
            if (v != null && v.SlotIdx >= 0)
            {
                DataObject d = new DataObject();
                d.SetData("LcdGlyph", v.SlotIdx);
                _vars.DoDragDrop(d, DragDropEffects.Copy);
            }
        };
        _varHost.Controls.Add(_vars);   // Fill 先加

        _footer = new Label();
        _footer.Text = "点行即插入当前行 · 值为实时预览";
        _footer.Dock = DockStyle.Bottom;
        _footer.Height = 22;
        _footer.ForeColor = Color.FromArgb(120, 126, 134);
        _footer.Font = new Font("Microsoft YaHei UI", 8f);
        _footer.TextAlign = ContentAlignment.MiddleLeft;
        _footer.BackColor = Color.FromArgb(20, 21, 24);
        _varHost.Controls.Add(_footer);

        _search = new TextBox();
        _search.Dock = DockStyle.Top;
        _search.Font = new Font("Microsoft YaHei UI", 9.5f);
        _search.BackColor = Color.FromArgb(38, 40, 46);
        _search.ForeColor = Color.FromArgb(235, 238, 242);
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.TextChanged += (s, e) => RefreshVarList();
        _varHost.Controls.Add(_search);

        Panel tabsHost = new Panel();
        tabsHost.Dock = DockStyle.Top;
        tabsHost.Height = 34;
        _varHost.Controls.Add(tabsHost);
        _tabsHost = tabsHost;
        _tabAll = MakeTab("全部", 0, tabsHost, 0);
        _tabBuiltin = MakeTab("内置", 1, tabsHost, 70);
        _tabHw = MakeTab("HWiNFO", 2, tabsHost, 140);

        Panel titleBar = new Panel();
        titleBar.Dock = DockStyle.Top;
        titleBar.Height = 26;
        titleBar.BackColor = _varHost.BackColor;
        _varHost.Controls.Add(titleBar);   // 最后加 = 最先 Dock
        _titleLabel = new Label();
        _titleLabel.Text = "数据台";
        _titleLabel.Location = new Point(0, 2);
        _titleLabel.AutoSize = true;
        _titleLabel.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
        titleBar.Controls.Add(_titleLabel);
        _btnCollapse = FlatBtn("◀", 30);
        _btnCollapse.Location = new Point(270, 1);
        _btnCollapse.Size = new Size(30, 22);
        _btnCollapse.Click += (s, e) => { _varsCollapsed = !_varsCollapsed; ApplyVarCollapse(); };
        titleBar.Controls.Add(_btnCollapse);

        // ---- 屏 ----
        _host = new Panel();
        _host.Dock = DockStyle.Top;
        _host.Height = 272;
        _host.BackColor = BackColor;
        _host.Resize += (s, e) => LayoutLcd();

        _lcd = new Lcd1602Control();
        _lcd.BackColor = BackColor;
        _lcd.GlyphDropped += OnGlyphDropped;
        _host.Controls.Add(_lcd);

        _btnBack = FlatBtn("背光 · 蓝", 96) ;
        _btnBack.Size = new Size(96, 28);
        _btnBack.Click += (s, e) =>
        {
            switch (_lcd.Backlight)
            {
                case Lcd1602Control.BacklightKind.Blue: _lcd.Backlight = Lcd1602Control.BacklightKind.Green; _btnBack.Text = BacklightText(_lcd.Backlight); break;
                case Lcd1602Control.BacklightKind.Green: _lcd.Backlight = Lcd1602Control.BacklightKind.Off; _btnBack.Text = BacklightText(_lcd.Backlight); break;
                default: _lcd.Backlight = Lcd1602Control.BacklightKind.Blue; _btnBack.Text = BacklightText(_lcd.Backlight); break;
            }
            RefreshVarList();
        };
        _host.Controls.Add(_btnBack);

        // ---- 编辑区 ----
        _editor = new Panel();
        _editor.Dock = DockStyle.Fill;
        _editor.BackColor = BackColor;
        _editor.Resize += (s, e) => LayoutEditor();

        // Dock 布局按集合逆序处理: Fill 最先加入, 其余依次
        Controls.Add(_editor);
        Controls.Add(_host);
        Controls.Add(_varHost);
        Controls.Add(_statusBar);

        _tb0 = MakeRow(0, "行1", out _chk0);
        _tb1 = MakeRow(1, "行2", out _chk1);

        _btnSave = FlatBtn("保存设置", 96);
        _btnSave.Click += (s, e) =>
        {
            SaveSettings();
            _saveFlashUntil = Environment.TickCount + 1600;
            _btnSave.Text = "已保存 ✓";
        };
        _editor.Controls.Add(_btnSave);

        _btnSync = FlatBtn("同步到真屏: 未连接", 172);
        _btnSync.Click += (s, e) =>
        {
            if (_sync.Connected)
            {
                _sync.Disconnect();
                _btnSync.Text = "同步到真屏: 未连接";
                _btnSync.BackColor = Color.FromArgb(58, 60, 66);
            }
            else if (_sync.Connect())
            {
                for (int i = 0; i < 8; i++) _sync.SendSlot(i);   // 同步全部自定义字形
                _btnSync.Text = "同步到真屏: 已同步 " + _sync.PortName;
                _btnSync.BackColor = Color.FromArgb(38, 84, 48);
            }
            else
            {
                _btnSync.Text = "同步到真屏: 未找到串口";
                _btnSync.BackColor = Color.FromArgb(88, 68, 28);
            }
        };
        _editor.Controls.Add(_btnSync);

        _btnPreset = FlatBtn("预设", 72);
        _btnPreset.Click += (s, e) =>
        {
            using (PresetForm f = new PresetForm(ListPresets, LoadPreset, SavePreset, DeletePreset))
                f.ShowDialog(this);
        };
        _editor.Controls.Add(_btnPreset);

        _btnGlyph = FlatBtn("点阵编辑器", 100);
        _btnGlyph.Click += (s, e) =>
        {
            using (GlyphForm f = new GlyphForm(1, slot =>
            {
                Render();
                if (_sync.Connected) _sync.SendSlot(slot);
            }))
                f.ShowDialog(this);
            SaveSettings();
        };
        _editor.Controls.Add(_btnGlyph);
    }

    private Button MakeTab(string text, int mode, Panel tabs, int x)
    {
        Button b = FlatBtn(text, 66);
        b.Location = new Point(x, 3);
        b.Tag = mode;
        b.Click += (s, e) => { _tabMode = mode; StyleTabs(); RefreshVarList(); };
        tabs.Controls.Add(b);
        return b;
    }

    private void StyleTabs()
    {
        Button[] bs = { _tabAll, _tabBuiltin, _tabHw };
        Color accent = VarPainter.Accent(_lcd.Backlight);
        foreach (Button b in bs)
        {
            int m = (int)b.Tag;
            b.BackColor = m == _tabMode ? Mix(accent, Color.FromArgb(58, 60, 66), 0.32f) : Color.FromArgb(58, 60, 66);
            b.ForeColor = m == _tabMode ? Color.White : Color.FromArgb(190, 193, 200);
        }
    }

    private static Color Mix(Color a, Color b, float k)
    {
        return Color.FromArgb(
            (int)(a.R * k + b.R * (1 - k)),
            (int)(a.G * k + b.G * (1 - k)),
            (int)(a.B * k + b.B * (1 - k)));
    }

    private static string BacklightText(Lcd1602Control.BacklightKind k)
    {
        if (k == Lcd1602Control.BacklightKind.Green) return "背光 · 绿";
        if (k == Lcd1602Control.BacklightKind.Off) return "背光 · 灭";
        return "背光 · 蓝";
    }

    private static Button FlatBtn(string text, int w)
    {
        Button b = new Button();
        b.Text = text;
        b.Size = new Size(w, 26);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(90, 96, 106);
        b.BackColor = Color.FromArgb(58, 60, 66);
        b.ForeColor = Color.FromArgb(230, 232, 236);
        return b;
    }

    private Label NewStatusLabel()
    {
        Label l = new Label();
        l.AutoSize = true;
        l.Font = new Font("Microsoft YaHei UI", 9f);
        l.ForeColor = Color.FromArgb(190, 193, 200);
        return l;
    }

    private TextBox MakeRow(int row, string caption, out CheckBox chk)
    {
        int y = 52 + row * RowH;   // 顶部 36px 留给 [保存][同步] 按钮条

        Label lab = new Label();
        lab.Text = caption;
        lab.Location = new Point(14, y + 4);
        lab.Size = new Size(RowLabelW, 24);
        lab.ForeColor = Color.FromArgb(190, 193, 200);

        TextBox tb = new TextBox();
        tb.Location = new Point(RowBoxX, y);
        tb.Font = new Font("Consolas", 11f);
        tb.BackColor = Color.FromArgb(22, 23, 27);
        tb.ForeColor = Color.FromArgb(235, 238, 242);
        tb.BorderStyle = BorderStyle.FixedSingle;
        tb.GotFocus += (s, e) => _focusedRow = row;
        tb.KeyUp += (s, e) => Render();

        CheckBox ck = new CheckBox();
        ck.Text = "滚动";
        ck.Location = new Point(100, y + 5);
        ck.Size = new Size(RowChkW, 22);
        ck.ForeColor = Color.FromArgb(190, 193, 200);
        ck.CheckedChanged += (s, e) =>
        {
            if (row == 0) _lcd.ScrollRow0 = ck.Checked; else _lcd.ScrollRow1 = ck.Checked;
        };
        chk = ck;

        Button btn = FlatBtn("插入变量", RowBtnW);
        btn.Location = new Point(100, y);
        btn.Click += (s, e) => { _focusedRow = row; tb.Focus(); OpenPalette(); };

        _editor.Controls.Add(lab);
        _editor.Controls.Add(tb);
        _editor.Controls.Add(ck);
        _editor.Controls.Add(btn);
        return tb;
    }

    private void LayoutLcd()
    {
        if (_lcd == null) return;
        int px = _lcd.PixelSize;
        int w = 16 * 6 * px + 34;
        int h = 2 * 8 * px + 40;
        _lcd.Size = new Size(w, h);
        _lcd.Location = new Point((_host.ClientSize.Width - w) / 2, 14);
        _btnBack.Location = new Point(_host.ClientSize.Width - 120, 18);
    }

    private void LayoutEditor()
    {
        if (_tb0 == null) return;
        int tbW = _editor.ClientSize.Width - RowBoxX - 176;
        if (tbW < 180) tbW = 180;
        _tb0.Size = new Size(tbW, 30);
        _tb0.Location = new Point(RowBoxX, 52);
        _tb1.Size = new Size(tbW, 30);
        _tb1.Location = new Point(RowBoxX, 52 + RowH);
        if (_btnSave != null)
        {
            _btnGlyph.Location = new Point(_editor.ClientSize.Width - 466, 14);
            _btnPreset.Location = new Point(_editor.ClientSize.Width - 360, 14);
            _btnSave.Location = new Point(_editor.ClientSize.Width - 282, 14);
            _btnSync.Location = new Point(_editor.ClientSize.Width - 178, 14);
        }
        foreach (Control c in _editor.Controls)
        {
            if (c is TextBox || c == _btnSave || c == _btnSync || c == _btnPreset || c == _btnGlyph) continue;
            if (c is Label) c.Location = new Point(14, c.Location.Y);
            else if (c is CheckBox) { int y = c.Location.Y; c.Location = new Point(RowBoxX + tbW + 12, y); }
            else if (c is Button) { int y = c.Location.Y; c.Location = new Point(RowBoxX + tbW + 84, y); }
        }
    }

    // ---------- 数据台 ----------

    private void RefreshVarList()
    {
        var groups = new List<List<VarItem>>();
        for (int i = 0; i < 5; i++) groups.Add(new List<VarItem>());

        string fanName;
        if (_eng.HwStateText.StartsWith("HWiNFO ●"))
            fanName = _eng.FanRpm >= 0 ? "风扇转速" : "风扇转速 (本机无传感器)";
        else
            fanName = "风扇转速 (需 HWiNFO)";
        groups[0].Add(VarItem.Builtin("CPU 占用", "{cpu}", "%", eng => FmtVal(eng.CpuPct)));
        groups[0].Add(VarItem.Builtin("SoC 温度 (核显)", "{soc}", "°C", eng => FmtVal(eng.SocC)));
        groups[0].Add(VarItem.Builtin("核显占用", "{gpu}", "%", eng => FmtVal(eng.GpuPct)));
        groups[0].Add(VarItem.Builtin("内存占用", "{ram}", "%", eng => FmtVal(eng.RamPct)));
        groups[0].Add(VarItem.Builtin(fanName, "{fan}", "RPM", eng => FmtVal(eng.FanRpm)));
        groups[0].Add(VarItem.Builtin("CPU 真实温度", "{temp}", "°C", eng => FmtVal(eng.CpuRealC)));

        int[] cap = { 0, 80, 80, 80, 0 };
        foreach (HwEntry e in _eng.HwItems)
        {
            if (e.Type == HwEntry.TypeTemp) AddToGroup(groups[1], e, cap, 1);
            else if (e.Type == HwEntry.TypeFan) AddToGroup(groups[2], e, cap, 2);
            else if (e.Type == HwEntry.TypeUsage) AddToGroup(groups[3], e, cap, 3);
        }
        for (int slot = 0; slot < 8; slot++) groups[4].Add(VarItem.Glyph(slot));

        // 命令面板用扁平源
        _varItems.Clear();
        foreach (List<VarItem> g in groups) _varItems.AddRange(g);

        string q = _search == null ? "" : _search.Text.Trim().ToLowerInvariant();
        bool searching = q.Length > 0;

        _vars.BeginUpdate();
        _vars.Items.Clear();
        for (int g = 0; g < 5; g++)
        {
            if (_tabMode == 1 && g != 0 && g != 4) continue;      // 内置: 常用+字形
            if (_tabMode == 2 && (g == 0 || g == 4)) continue;    // HWiNFO: 温/扇/占
            List<VarItem> hits = new List<VarItem>();
            bool[] added = new bool[groups[g].Count];
            for (int i = 0; i < groups[g].Count; i++)
            {
                VarItem v = groups[g][i];
                if (searching && !v.Name.ToLowerInvariant().Contains(q)
                    && !v.Token.ToLowerInvariant().Contains(q)) continue;
                hits.Add(v);
            }
            if (hits.Count == 0) continue;
            bool collapsed = _grpCollapsed[g] && !searching;
            _vars.Items.Add(new VarItem
            {
                IsGroup = true, GroupIndex = g, Name = GrpTitles[g],
                Count = hits.Count, Collapsed = collapsed, Token = null
            });
            if (!collapsed)
                foreach (VarItem v in hits) _vars.Items.Add(v);
        }
        if (_vars.Items.Count == 0)
            _vars.Items.Add(new VarItem
            {
                Cat = _tabMode == 2 ? VarItem.CatHw : VarItem.CatBuiltin,
                Badge = BadgeKind.Inner,
                Name = _tabMode == 2 ? "HWiNFO 未接通 · 点击更新" : "没有匹配的数据 · 输入内容试试",
                Token = null
            });
        _vars.EndUpdate();
        StyleTabs();
    }

    private static void AddToGroup(List<VarItem> g, HwEntry e, int[] cap, int groupIndex)
    {
        if (g.Count >= cap[groupIndex]) return;
        g.Add(VarItem.Hw(e));
    }

    private void DrawVarRow(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _vars.Items.Count) return;
        VarItem v = _vars.Items[e.Index] as VarItem;
        if (v == null) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (v.IsGroup)
        {
            VarPainter.DrawGroupRow(g, e.Bounds, v, UiFonts.Name);
            return;
        }
        bool sel = (e.State & DrawItemState.Selected) != 0;
        VarPainter.DrawRow(g, e.Bounds, v, _eng, VarPainter.Accent(_lcd.Backlight), sel,
            e.Index == _hoverVar && !sel, UiFonts.Name, UiFonts.Value);
    }

    private void OnVarPicked(object sender, EventArgs e)
    {
        VarItem v = _vars.SelectedItem as VarItem;
        if (v == null) { _vars.SelectedIndex = -1; return; }
        if (v.IsGroup)
        {
            _grpCollapsed[v.GroupIndex] = !_grpCollapsed[v.GroupIndex];
            _vars.SelectedIndex = -1;
            RefreshVarList();
            return;
        }
        if (v.Token == null)
        {
            if (v.Name.IndexOf("HWiNFO") >= 0) OpenHwGuide();
            _vars.SelectedIndex = -1;
            return;
        }
        InsertToken(CurrentBox(), v.Token);
        _vars.SelectedIndex = -1;
        Render();
    }

    // 字形拖放到模拟屏: 在对应行的对应模板位置插入占位符
    private void OnGlyphDropped(int slot, int col, int row)
    {
        TextBox tb = row == 1 ? _tb1 : _tb0;
        if (tb == null) return;
        List<int> map = new List<int>();
        RenderLine(tb.Text, _eng, ref map);
        int idx = (col >= 0 && col < map.Count) ? map[col] : tb.Text.Length;
        string token = "{g" + (slot + 1) + "}";
        tb.Text = tb.Text.Insert(idx, token);
        tb.SelectionStart = idx + token.Length;
        tb.Focus();
        Render();
    }

    private void ApplyVarCollapse()
    {
        _varHost.Width = _varsCollapsed ? 36 : 312;
        bool show = !_varsCollapsed;
        _vars.Visible = show;
        _search.Visible = show;
        _tabsHost.Visible = show;
        _footer.Visible = show;
        _titleLabel.Visible = show;
        _btnCollapse.Text = _varsCollapsed ? "▶" : "◀";
        _btnCollapse.Location = _varsCollapsed ? new Point(3, 1) : new Point(_varHost.Width - 36, 1);
    }

    private void OpenPalette()
    {
        if (_varItems.Count == 0) RefreshVarList();
        using (CommandPaletteForm f = new CommandPaletteForm(_varItems, _eng,
            token => { InsertToken(CurrentBox(), token); Render(); }))
        {
            f.Owner = this;
            f.ShowDialog(this);
        }
    }

    private TextBox CurrentBox() { return _focusedRow == 1 ? _tb1 : _tb0; }

    private static void InsertToken(TextBox tb, string token)
    {
        int i = tb.SelectionStart;
        tb.Text = tb.Text.Insert(i, token);
        tb.SelectionStart = i + token.Length;
    }

    // ---------- 渲染 ----------

    private void Render()
    {
        string l0 = RenderLine(_tb0.Text);
        string l1 = RenderLine(_tb1.Text);
        _lcd.SetText(l0, l1);
        if (_lcd.ScrollRow0 != _chk0.Checked) _lcd.ScrollRow0 = _chk0.Checked;
        if (_lcd.ScrollRow1 != _chk1.Checked) _lcd.ScrollRow1 = _chk1.Checked;
    }

    private string RenderLine(string s) { return RenderLine(s, _eng); }

    internal static string RenderLine(string s, DataEngine eng)
    {
        List<int> map = null;
        return RenderLine(s, eng, ref map);
    }

    /// <summary>渲染一行; map 记录每个渲染字符对应的模板源索引(供拖放定位)</summary>
    internal static string RenderLine(string s, DataEngine eng, ref List<int> map)
    {
        if (s == null) s = "";
        if (map != null) map.Clear();
        StringBuilder sb = new StringBuilder(s.Length + 8);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '{')
            {
                int j = s.IndexOf('}', i);
                if (j > i)
                {
                    string tok = RenderToken(s.Substring(i + 1, j - i - 1), eng);
                    sb.Append(tok);
                    if (map != null)
                        for (int k = 0; k < tok.Length; k++) map.Add(i);
                    i = j + 1;
                    continue;
                }
            }
            sb.Append(c);
            if (map != null) map.Add(i);
            i++;
        }
        return sb.ToString();
    }

    private static string RenderToken(string name, DataEngine eng)
    {
        switch (name)
        {
            case "cpu": return FmtVal(eng.CpuPct);
            case "soc": return FmtVal(eng.SocC);
            case "gpu": return FmtVal(eng.GpuPct);
            case "ram": return FmtVal(eng.RamPct);
            case "fan": return FmtVal(eng.FanRpm);
            case "temp": return FmtVal(eng.CpuRealC);
            default:
                if (name.Length == 2 && name[0] == 'g' && name[1] >= '1' && name[1] <= '8')
                    return ((char)(name[1] - '0')).ToString();   // {g1}~{g8} 自定义字形
                if (name.StartsWith("h|", StringComparison.Ordinal))
                {
                    string v = eng.FindHw(name);
                    return v ?? "--";
                }
                return "-?-";
        }
    }

    private static string FmtVal(int v) { return v < 0 ? "--" : (v < 10 ? "0" + v : v.ToString()); }   // 两位数防跳动

    // ---------- 状态 ----------

    private void OnTick(object sender, EventArgs e)
    {
        _eng.Tick();
        Render();
        UpdateStatus();
        RefreshVarList();
        // 同步到真屏: 每秒下发渲染好的两行
        if (_sync.Connected)
        {
            _sync.SendRow(0, _chk0.Checked, RenderLine(_tb0.Text));
            _sync.SendRow(1, _chk1.Checked, RenderLine(_tb1.Text));
        }
        if (_saveFlashUntil != 0 && Environment.TickCount > _saveFlashUntil)
        {
            _saveFlashUntil = 0;
            _btnSave.Text = "保存设置";
        }
    }

    private void UpdateStatus()
    {
        _lhmStatus.ForeColor = _eng.LhmOk ? Color.FromArgb(120, 210, 120) : Color.FromArgb(225, 130, 110);
        _lhmStatus.Text = _eng.LhmOk ? "数据引擎 · LHM ●  (CPU/SoC/内存/占用)" : "数据引擎 · LHM ○ 已切系统计数器兜底";
        if (_eng.HwStateText.StartsWith("HWiNFO ●")) _hwStatus.ForeColor = Color.FromArgb(120, 210, 120);
        else if (_eng.HwStateText.StartsWith("HWiNFO 已运行")) _hwStatus.ForeColor = Color.FromArgb(240, 180, 90);
        else _hwStatus.ForeColor = Color.FromArgb(150, 153, 160);
        _hwStatus.Text = _eng.HwStateText;
        if (!_eng.HwStateText.StartsWith("HWiNFO ●")) _hwStatus.Text += " · 点击安装引导";
        else _hwStatus.Text += " · 数据已接通";
    }

    private void OpenHwGuide()
    {
        using (HwGuideForm f = new HwGuideForm())
            f.ShowDialog(this);
        _eng.Tick();
        UpdateStatus();
        RefreshVarList();
        Render();
    }

    // ---------- 设置持久化 ----------

    private string SettingsFile
    {
        get
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LCD1602Studio");
            Directory.CreateDirectory(d);
            return Path.Combine(d, "settings.ini");
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            foreach (string ln in File.ReadAllLines(SettingsFile))
            {
                int eq = ln.IndexOf('=');
                if (eq <= 0) continue;
                string k = ln.Substring(0, eq).Trim();
                string v = ln.Substring(eq + 1);
                if (k == "line0") _tb0.Text = v;
                else if (k == "line1") _tb1.Text = v;
                else if (k == "scroll0") _chk0.Checked = v == "1";
                else if (k == "scroll1") _chk1.Checked = v == "1";
                else if (k == "backlight")
                {
                    int b;
                    if (int.TryParse(v, out b) && b >= 0 && b <= 2)
                    {
                        _lcd.Backlight = (Lcd1602Control.BacklightKind)b;
                        _btnBack.Text = BacklightText(_lcd.Backlight);
                    }
                }
                else if (k == "varsCollapsed") _varsCollapsed = v == "1";
                else if (k == "groups")
                {
                    for (int i = 0; i < v.Length && i < 5; i++)
                        if (v[i] == '1') _grpCollapsed[i] = true;
                }
                else if (k.StartsWith("glyph"))
                {
                    int g;
                    if (int.TryParse(k.Substring(5), out g) && g >= 0 && g < 8)
                        CustomGlyphs.SetFromHex(g, v);
                }
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("line0=" + _tb0.Text);
            sb.AppendLine("line1=" + _tb1.Text);
            sb.AppendLine("scroll0=" + (_chk0.Checked ? "1" : "0"));
            sb.AppendLine("scroll1=" + (_chk1.Checked ? "1" : "0"));
            sb.AppendLine("backlight=" + (int)_lcd.Backlight);
            sb.AppendLine("varsCollapsed=" + (_varsCollapsed ? "1" : "0"));
            StringBuilder gb = new StringBuilder();
            for (int i = 0; i < 5; i++) gb.Append(_grpCollapsed[i] ? "1" : "0");
            sb.AppendLine("groups=" + gb.ToString());
            for (int i = 0; i < 8; i++) sb.AppendLine("glyph" + i + "=" + CustomGlyphs.ToHex(i));
            File.WriteAllText(SettingsFile, sb.ToString());
        }
        catch { }
    }

    // ---------- 预设 ----------

    internal static Action<string> CurrentRowInserter;

    private string PresetDir
    {
        get
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LCD1602Studio", "presets");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string Sanitize(string n)
    {
        if (n == null) return "unnamed";
        char[] bad = Path.GetInvalidFileNameChars();
        StringBuilder sb = new StringBuilder(n.Length);
        foreach (char c in n)
        {
            bool illegal = false;
            foreach (char b in bad) if (c == b) illegal = true;
            sb.Append(illegal ? '_' : c);
        }
        return sb.ToString();
    }

    private List<string> ListPresets()
    {
        List<string> list = new List<string>();
        foreach (string f in Directory.GetFiles(PresetDir, "*.ini"))
            list.Add(Path.GetFileNameWithoutExtension(f));
        list.Sort();
        return list;
    }

    private void SavePreset(string name)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("line0=" + _tb0.Text);
        sb.AppendLine("line1=" + _tb1.Text);
        sb.AppendLine("scroll0=" + (_chk0.Checked ? "1" : "0"));
        sb.AppendLine("scroll1=" + (_chk1.Checked ? "1" : "0"));
        sb.AppendLine("backlight=" + (int)_lcd.Backlight);
        File.WriteAllText(Path.Combine(PresetDir, Sanitize(name) + ".ini"), sb.ToString());
    }

    private void LoadPreset(string name)
    {
        string f = Path.Combine(PresetDir, Sanitize(name) + ".ini");
        if (!File.Exists(f)) return;
        foreach (string ln in File.ReadAllLines(f))
        {
            int eq = ln.IndexOf('=');
            if (eq <= 0) continue;
            string k = ln.Substring(0, eq).Trim();
            string v = ln.Substring(eq + 1);
            if (k == "line0") _tb0.Text = v;
            else if (k == "line1") _tb1.Text = v;
            else if (k == "scroll0") _chk0.Checked = v == "1";
            else if (k == "scroll1") _chk1.Checked = v == "1";
            else if (k == "backlight")
            {
                int b;
                if (int.TryParse(v, out b) && b >= 0 && b <= 2)
                {
                    _lcd.Backlight = (Lcd1602Control.BacklightKind)b;
                    _btnBack.Text = BacklightText(_lcd.Backlight);
                }
            }
        }
        Render();
        RefreshVarList();
    }

    private void DeletePreset(string name)
    {
        string f = Path.Combine(PresetDir, Sanitize(name) + ".ini");
        if (File.Exists(f)) File.Delete(f);
    }

    // ---------- 自检 ----------

    internal static int RunSelfTest()
    {
        string outDir = Path.Combine(Path.GetTempPath(), "LCD1602Studio_selftest");
        Directory.CreateDirectory(outDir);
        try
        {
            MainForm f = new MainForm(true);
            f.ApplySample();
            f.Render();
            f.CreateControl();

            int px = f._lcd.PixelSize;
            f._lcd.Size = new Size(16 * 6 * px + 34, 2 * 8 * px + 40);
            f._lcd.CreateControl();
            Bitmap bmp = new Bitmap(f._lcd.Width, f._lcd.Height);
            f._lcd.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            bmp.Save(Path.Combine(outDir, "selftest.png"));
            bmp.Dispose();

            // 整窗截图(验证布局/数据台): PrintWindow 精确捕获, 含子控件
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(80, 80);
            f.Show();
            Application.DoEvents();
            System.Threading.Thread.Sleep(600);
            Application.DoEvents();
            Bitmap ui = new Bitmap(f.Width, f.Height);
            using (Graphics g = Graphics.FromImage(ui))
            {
                IntPtr hdc = g.GetHdc();
                PrintWindow(f.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */);
                g.ReleaseHdc(hdc);
            }
            ui.Save(Path.Combine(outDir, "selftest_ui.png"));
            ui.Dispose();
            f.Hide();

            string l0 = f._lcd.RowText(0), l1 = f._lcd.RowText(1);
            f._eng.Tick();
            File.WriteAllText(Path.Combine(outDir, "selftest.txt"),
                "line0=[" + l0 + "]\nline1=[" + l1 + "]\nfan=" + f._eng.FanRpm + " temp=" + f._eng.CpuRealC + "\n"
                + "lhmOk=" + f._eng.LhmOk + " realCpu=" + f._eng.CpuPct + "% realRam=" + f._eng.RamPct
                + "% realSoc=" + f._eng.SocC + "C hw=" + f._eng.HwCount + "\n"
                + "bounds editor=" + f.GetEditorBounds() + " host=" + f.GetHostBounds()
                + " vars=" + f.GetVarsBounds() + " status=" + f.GetStatusBounds() + "\n");
            bool ok = l0.StartsWith("CPU 37%") && l1.Contains("56") && l1.Contains("hello")
                && f._eng.LhmOk;
            f._eng.Dispose();
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outDir, "selftest.err.txt"), ex.ToString());
            return 2;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    private void ApplySample()
    {
        _eng.CpuPct = 37; _eng.SocC = 48; _eng.GpuPct = 12; _eng.RamPct = 63;
        _eng.FanRpm = -1; _eng.CpuRealC = 56;
        _eng.HwItems = new List<HwEntry>();
        _eng.HwItems.Add(new HwEntry { Sensor = "CPU", Reading = "CPU Fan", Unit = "RPM", Type = 3, Value = 3400 });
        _eng.HwItems.Add(new HwEntry { Sensor = "CPU", Reading = "CPU Package", Unit = "°C", Type = 1, Value = 56 });
        _tb0.Text = "CPU {cpu}% {soc}℃ 扇 {fan}RPM";
        _tb1.Text = "{temp}℃ RAM {ram}% hello 1602";
    }
}

// HWiNFO 安装引导(何时用: HWiNFO 未运行或共享内存未开启时)
internal class HwGuideForm : Form
{
    public HwGuideForm()
    {
        Text = "HWiNFO 数据源引导";
        ClientSize = new Size(560, 280);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(30, 31, 36);
        ForeColor = Color.FromArgb(224, 226, 230);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        Label t = new Label();
        t.Text = "想让模拟器显示风扇转速 / CPU 真实温度?装一次 HWiNFO:\r\n"
            + "  1. 点下方按钮打开官网,下载 HWiNFO64(免费)并安装\r\n"
            + "  2. 打开 HWiNFO64,启动左侧的传感器窗口(可最小化到托盘)\r\n"
            + "  3. 设置 → General → 勾选 Shared Memory Support\r\n"
            + "  4. 回到本软件,数据会自动出现,无需重启\r\n\r\n"
            + "提示: HWiNFO 是闭源软件,本软件只是读取它公开的共享内存接口,\r\n"
            + "不会捆绑它的安装包。";
        t.Location = new Point(16, 14);
        t.Size = new Size(528, 170);
        Controls.Add(t);

        Button b1 = FlatButton("打开 HWiNFO 官网下载");
        b1.Location = new Point(16, 200);
        b1.Size = new Size(190, 32);
        b1.Click += (s, e) => { try { Process.Start("https://www.hwinfo.com/download/"); } catch { } };
        Controls.Add(b1);

        Button b2 = FlatButton("关闭");
        b2.Location = new Point(220, 200);
        b2.Size = new Size(90, 32);
        b2.Click += (s, e) => Close();
        Controls.Add(b2);
    }

    private static Button FlatButton(string text)
    {
        Button b = new Button();
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(90, 96, 106);
        b.BackColor = Color.FromArgb(58, 60, 66);
        b.ForeColor = Color.FromArgb(230, 232, 236);
        return b;
    }
}
