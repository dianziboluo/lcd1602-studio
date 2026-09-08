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
    private ModuleHost _modules = new ModuleHost();
    private Lcd1602Control _lcd;
    private Panel _host, _editor, _varHost, _statusBar;
    private TextBox _tb0, _tb1, _search;
    private CheckBox _chk0, _chk1;
    private ListBox _vars;
    private Label _lhmStatus, _hwStatus, _footer, _modStatus, _titleLabel, _warn;
    private UiButton _btnBack, _tabAll, _tabBuiltin, _tabHw, _btnCollapse, _btnSave, _btnSync, _btnPreset, _btnGlyph;
    private Panel _tabsHost;
    private UiField _field0, _field1;
    private string _varSig = "";
    private int _warnUntil;
    private Timer _timer;
    private NotifyIcon _tray;
    private bool _reallyClose, _trayHintShown;
    private int _focusedRow, _hoverVar = -1, _tabMode;   // 0 全部 1 内置 2 HWiNFO
    private int _saveFlashUntil;
    private readonly bool _selftest;
    private readonly List<VarItem> _varItems = new List<VarItem>();
    private readonly bool[] _grpCollapsed = new bool[7] { false, true, true, true, false, false, false };
    private bool _varsCollapsed;
    private static readonly string[] GrpTitles = { "常用", "HWiNFO · 温度", "HWiNFO · 风扇", "HWiNFO · 占用", "字形 · 拖到屏上", "DSH", "DeepSeek" };

    private const int RowLabelW = 48, RowBoxX = 66, RowChkW = 64, RowBtnW = 92, RowH = 52;
    private const int VarHostW = 340;

    public MainForm(bool selftest)
    {
        _selftest = selftest;
        Text = "LCD1602 Studio — 桌上的一点光";
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(1120, 720);
        BackColor = UiTheme.Bg;
        ForeColor = UiTheme.Text;
        Font = UiTheme.FontUi;
        DoubleBuffered = true;

        BuildUI();
        LoadSettings();
        _eng.Open();
        // 扩展模块: PC 端一切功能以模块接入, 显示器只负责显示
        _modules.Add(new DshModule());
        _modules.Add(new DsModule());
        _modules.WriteSampleConfigIfMissing();
        _modules.Start();
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
            _modules.Dispose();
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
        _statusBar.Height = 42;
        _statusBar.BackColor = Color.FromArgb(17, 18, 21);
        _statusBar.Paint += (s, e) =>
        {
            using (Pen p = new Pen(UiTheme.Border))
                e.Graphics.DrawLine(p, 0, 0, _statusBar.Width, 0);
        };

        _lhmStatus = NewStatusLabel();
        _lhmStatus.Location = new Point(16, 12);
        _statusBar.Controls.Add(_lhmStatus);
        _hwStatus = NewStatusLabel();
        _hwStatus.Location = new Point(420, 12);
        _hwStatus.Cursor = Cursors.Hand;
        _hwStatus.Click += (s, e) => OpenHwGuide();
        _statusBar.Controls.Add(_hwStatus);

        _modStatus = NewStatusLabel();
        _modStatus.Location = new Point(800, 12);
        _statusBar.Controls.Add(_modStatus);

        // ---- 数据台 ----
        _varHost = new Panel();
        _varHost.Dock = DockStyle.Right;
        _varHost.Width = 340;
        _varHost.BackColor = UiTheme.Surface;
        _varHost.Padding = new Padding(12, 10, 12, 6);
        Controls.Add(_varHost);

        _vars = new ListBox();
        _vars.Dock = DockStyle.Fill;
        _vars.DrawMode = DrawMode.OwnerDrawFixed;
        _vars.ItemHeight = 32;
        _vars.BackColor = UiTheme.Surface;
        _vars.BorderStyle = BorderStyle.None;
        _vars.Font = UiTheme.FontUi;
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
        SetDoubleBuffered(_vars);       // 消除自绘列表每秒重绘的闪动

        _footer = new Label();
        _footer.Text = "点行即插入当前行 · 值为实时预览";
        _footer.Dock = DockStyle.Bottom;
        _footer.Height = 24;
        _footer.ForeColor = UiTheme.TextFaint;
        _footer.Font = UiTheme.FontUiSmall;
        _footer.TextAlign = ContentAlignment.MiddleLeft;
        _footer.BackColor = UiTheme.Surface;
        _varHost.Controls.Add(_footer);

        _search = new TextBox();
        _search.Dock = DockStyle.Top;
        _search.Height = 30;
        _search.Font = UiTheme.FontUi;
        _search.BackColor = UiTheme.SurfaceAlt;
        _search.ForeColor = UiTheme.Text;
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.TextChanged += (s, e) => RefreshVarList();
        _varHost.Controls.Add(_search);

        Panel tabsHost = new Panel();
        tabsHost.Dock = DockStyle.Top;
        tabsHost.Height = 40;
        tabsHost.BackColor = UiTheme.Surface;
        _varHost.Controls.Add(tabsHost);
        _tabsHost = tabsHost;
        _tabAll = MakeTab("全部", 0, tabsHost, 0);
        _tabBuiltin = MakeTab("内置", 1, tabsHost, 78);
        _tabHw = MakeTab("HWiNFO", 2, tabsHost, 156);

        Panel titleBar = new Panel();
        titleBar.Dock = DockStyle.Top;
        titleBar.Height = 30;
        titleBar.BackColor = UiTheme.Surface;
        _varHost.Controls.Add(titleBar);   // 最后加 = 最先 Dock
        _titleLabel = new Label();
        _titleLabel.Text = "数据台";
        _titleLabel.Location = new Point(0, 5);
        _titleLabel.AutoSize = true;
        _titleLabel.Font = UiTheme.FontUiBold;
        _titleLabel.ForeColor = UiTheme.Text;
        titleBar.Controls.Add(_titleLabel);
        _btnCollapse = new UiButton("◀", 30, 24);
        _btnCollapse.Location = new Point(284, 2);
        _btnCollapse.Click += (s, e) => { _varsCollapsed = !_varsCollapsed; ApplyVarCollapse(); };
        titleBar.Controls.Add(_btnCollapse);

        // ---- 屏 ----
        _host = new Panel();
        _host.Dock = DockStyle.Top;
        _host.Height = 300;
        _host.BackColor = UiTheme.Bg;
        _host.Resize += (s, e) => LayoutLcd();

        _lcd = new Lcd1602Control();
        _lcd.BackColor = UiTheme.Bg;
        _lcd.GlyphDropped += OnGlyphDropped;
        _host.Controls.Add(_lcd);

        _btnBack = new UiButton("背光 · 蓝", 108, 30);
        _btnBack.Click += (s, e) =>
        {
            switch (_lcd.Backlight)
            {
                case Lcd1602Control.BacklightKind.Blue: _lcd.Backlight = Lcd1602Control.BacklightKind.Green; break;
                case Lcd1602Control.BacklightKind.Green: _lcd.Backlight = Lcd1602Control.BacklightKind.Off; break;
                default: _lcd.Backlight = Lcd1602Control.BacklightKind.Blue; break;
            }
            ApplyAccent();
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

        _btnSave = new UiButton("保存设置", 100, 30);
        _btnSave.Click += (s, e) =>
        {
            SaveSettings();
            _saveFlashUntil = Environment.TickCount + 1600;
            _btnSave.Text = "已保存 ✓";
        };
        _editor.Controls.Add(_btnSave);

        _btnSync = new UiButton("同步到真屏: 未连接", 190, 30);
        _btnSync.Click += (s, e) =>
        {
            if (_sync.Connected)
            {
                _sync.Disconnect();
                _btnSync.Text = "同步到真屏: 未连接";
                _btnSync.Active = false;
            }
            else if (_sync.Connect())
            {
                for (int i = 0; i < 8; i++) _sync.SendSlot(i);   // 同步全部自定义字形
                _btnSync.Text = "同步到真屏: 已同步 " + _sync.PortName;
                _btnSync.Active = true;
            }
            else
            {
                _btnSync.Text = "同步到真屏: 未找到串口";
                _btnSync.Active = false;
            }
            _btnSync.Invalidate();
        };
        _editor.Controls.Add(_btnSync);

        _btnPreset = new UiButton("预设", 80, 30);
        _btnPreset.Click += (s, e) =>
        {
            using (PresetForm f = new PresetForm(ListPresets, LoadPreset, SavePreset, DeletePreset))
                f.ShowDialog(this);
        };
        _editor.Controls.Add(_btnPreset);

        _btnGlyph = new UiButton("点阵编辑器", 108, 30);
        _btnGlyph.Click += (s, e) =>
        {
            using (GlyphForm f = new GlyphForm(1, slot =>
            {
                Render();
                if (_sync.Connected) _sync.SendSlot(slot);
            }))
                f.ShowDialog(this);
            SaveSettings();
            RefreshVarList();
        };
        _editor.Controls.Add(_btnGlyph);

        // 非 ASCII 输入提示(1602 物理上只支持 ASCII)
        _warn = new Label();
        _warn.Text = "⚠ 1602 只支持 ASCII 字符(℃ 与 ° 例外),已自动忽略其它非 ASCII 输入";
        _warn.AutoSize = true;
        _warn.ForeColor = UiTheme.Warn;
        _warn.Font = UiTheme.FontUiSmall;
        _warn.Visible = false;
        _editor.Controls.Add(_warn);

        ApplyAccent();
    }

    /// <summary>强调色跟随背光: 按钮/数据台/标题一起变</summary>
    private void ApplyAccent()
    {
        Color accent = UiTheme.Accent(_lcd.Backlight);
        UiButton[] bs = { _btnBack, _tabAll, _tabBuiltin, _tabHw, _btnCollapse, _btnSave, _btnSync, _btnPreset, _btnGlyph };
        foreach (UiButton b in bs) b.SetAccent(accent);
        _btnBack.Text = BacklightText(_lcd.Backlight);
        StyleTabs();
        _vars.Invalidate();
    }

    private UiButton MakeTab(string text, int mode, Panel tabs, int x)
    {
        UiButton b = new UiButton(text, 72, 30);
        b.Location = new Point(x, 5);
        b.Tag = mode;
        b.Click += (s, e) => { _tabMode = mode; StyleTabs(); RefreshVarList(); };
        tabs.Controls.Add(b);
        return b;
    }

    private void StyleTabs()
    {
        UiButton[] bs = { _tabAll, _tabBuiltin, _tabHw };
        foreach (UiButton b in bs)
        {
            b.Active = (int)b.Tag == _tabMode;
            b.Invalidate();
        }
    }

    private static string BacklightText(Lcd1602Control.BacklightKind k)
    {
        if (k == Lcd1602Control.BacklightKind.Green) return "背光 · 绿";
        if (k == Lcd1602Control.BacklightKind.Off) return "背光 · 灭";
        return "背光 · 蓝";
    }

    /// <summary>给控件开启双缓冲(消除自绘控件闪动)</summary>
    private static void SetDoubleBuffered(Control c)
    {
        try
        {
            typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(c, true, null);
        }
        catch { }
    }

    private Label NewStatusLabel()
    {
        Label l = new Label();
        l.AutoSize = true;
        l.Font = UiTheme.FontUiSmall;
        l.ForeColor = UiTheme.TextDim;
        return l;
    }

    private TextBox MakeRow(int row, string caption, out CheckBox chk)
    {
        int y = 56 + row * RowH;   // 顶部 46px 留给按钮条

        Label lab = new Label();
        lab.Text = caption;
        lab.Location = new Point(14, y + 8);
        lab.Size = new Size(RowLabelW, 22);
        lab.ForeColor = UiTheme.TextDim;

        UiField field = new UiField();
        field.Location = new Point(RowBoxX, y);
        field.Size = new Size(300, 38);
        TextBox tb = field.Box;
        tb.GotFocus += (s, e) => _focusedRow = row;
        tb.KeyUp += (s, e) => Render();
        tb.TextChanged += (s, e) => FilterAscii(tb);
        if (row == 0) _field0 = field; else _field1 = field;

        CheckBox ck = new CheckBox();
        ck.Text = "滚动";
        ck.Location = new Point(100, y + 9);
        ck.Size = new Size(RowChkW, 22);
        ck.ForeColor = UiTheme.TextDim;
        ck.BackColor = Color.Transparent;
        ck.CheckedChanged += (s, e) =>
        {
            if (row == 0) _lcd.ScrollRow0 = ck.Checked; else _lcd.ScrollRow1 = ck.Checked;
        };
        chk = ck;

        UiButton btn = new UiButton("插入变量", RowBtnW, 30);
        btn.Location = new Point(100, y + 4);
        btn.Click += (s, e) => { _focusedRow = row; tb.Focus(); OpenPalette(); };

        _editor.Controls.Add(lab);
        _editor.Controls.Add(field);
        _editor.Controls.Add(ck);
        _editor.Controls.Add(btn);
        return tb;
    }

    // ---------- 非 ASCII 过滤(1602 只支持 ASCII; ℃ 与 ° 例外) ----------

    private static bool IsAsciiAllowed(char c)
    {
        return c < 128 || c == '\u2103' || c == '\u00B0';
    }

    /// <summary>过滤掉 1602 无法显示的字符(供自检直接测试)</summary>
    internal static string FilterAsciiText(string s, out bool removed)
    {
        removed = false;
        if (s == null) return "";
        StringBuilder sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (IsAsciiAllowed(c)) sb.Append(c);
            else removed = true;
        }
        return sb.ToString();
    }

    private bool _filtering;

    private void FilterAscii(TextBox tb)
    {
        if (_filtering) return;
        bool removed;
        string clean = FilterAsciiText(tb.Text, out removed);
        if (!removed) return;

        int caret = tb.SelectionStart;
        int newCaret = 0;
        for (int i = 0; i < caret && i < tb.Text.Length; i++)
            if (IsAsciiAllowed(tb.Text[i])) newCaret++;

        _filtering = true;
        tb.Text = clean;
        tb.SelectionStart = Math.Min(newCaret, tb.Text.Length);
        _filtering = false;

        _warnUntil = Environment.TickCount + 4000;
        _warn.Location = new Point(RowBoxX, Math.Max(2, _editor.ClientSize.Height - 28));
        _warn.Visible = true;
        Render();
    }

    private void LayoutLcd()
    {
        if (_lcd == null) return;
        // 屏尽可能大: 按可用宽度算点阵像素(向上取整), 上限 13
        int avail = _host.ClientSize.Width - 40;
        int px = (avail + 80) / 96;
        if (px > 13) px = 13;
        if (px < 5) px = 5;
        while (px > 5 && Lcd1602Control.FrameSize(px).Width > _host.ClientSize.Width - 20) px--;
        _lcd.PixelSize = px;
        Size sz = Lcd1602Control.FrameSize(px);
        _lcd.Size = sz;
        _lcd.Location = new Point((_host.ClientSize.Width - sz.Width) / 2, 16);
        _btnBack.Location = new Point(_host.ClientSize.Width - 128, 20);
        int wantH = sz.Height + 84;
        if (_host.Height != wantH && wantH > 180) _host.Height = wantH;
    }

    private void LayoutEditor()
    {
        if (_tb0 == null) return;
        int tbW = _editor.ClientSize.Width - RowBoxX - 200;
        if (tbW < 220) tbW = 220;
        _field0.Size = new Size(tbW, 38);
        _field0.Location = new Point(RowBoxX, 56);
        _field1.Size = new Size(tbW, 38);
        _field1.Location = new Point(RowBoxX, 56 + RowH);
        if (_btnSave != null)
        {
            _btnGlyph.Location = new Point(_editor.ClientSize.Width - 494, 14);
            _btnPreset.Location = new Point(_editor.ClientSize.Width - 378, 14);
            _btnSave.Location = new Point(_editor.ClientSize.Width - 290, 14);
            _btnSync.Location = new Point(_editor.ClientSize.Width - 182, 14);
        }
        foreach (Control c in _editor.Controls)
        {
            if (c is UiField || c == _btnSave || c == _btnSync || c == _btnPreset || c == _btnGlyph) continue;
            if (c is Label && c != _warn) c.Location = new Point(14, c.Location.Y);
            else if (c is CheckBox) { int y = c.Location.Y; c.Location = new Point(RowBoxX + tbW + 14, y); }
            else if (c is UiButton) { int y = c.Location.Y; c.Location = new Point(RowBoxX + tbW + 88, y); }
        }
    }

    // ---------- 数据台 ----------

    private void RefreshVarList()
    {
        var groups = new List<List<VarItem>>();
        for (int i = 0; i < 7; i++) groups.Add(new List<VarItem>());

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

        // 扩展模块变量(DSH / DeepSeek ...)
        int gi = 5;
        foreach (IModule m in _modules.Modules)
        {
            if (gi >= groups.Count) break;
            foreach (ModuleVar mv in m.Variables())
            {
                ModuleVar captured = mv;
                VarItem it = VarItem.Builtin(captured.Name, "{" + captured.Token + "}", captured.Unit,
                    eng => SafeModuleValue(captured));
                groups[gi].Add(it);
            }
            gi++;
        }

        // 命令面板用扁平源
        _varItems.Clear();
        foreach (List<VarItem> g in groups) _varItems.AddRange(g);

        string q = _search == null ? "" : _search.Text.Trim().ToLowerInvariant();
        bool searching = q.Length > 0;

        // 结构签名: 未变化则只重绘(刷新实时值), 不重建列表 —— 消除每秒闪动与滚动条跳动
        StringBuilder sig = new StringBuilder();
        sig.Append(_tabMode).Append('|').Append(q).Append('|');
        for (int i = 0; i < _grpCollapsed.Length; i++) sig.Append(_grpCollapsed[i] ? '1' : '0');
        sig.Append('|').Append(fanName).Append('|').Append(_eng.HwCount);
        foreach (List<VarItem> g in groups)
        {
            sig.Append('#').Append(g.Count);
            foreach (VarItem it in g) sig.Append(it.Token);
        }
        string signature = sig.ToString();
        if (signature == _varSig)
        {
            _vars.Invalidate();   // 只刷新数值
            return;
        }
        _varSig = signature;

        _vars.BeginUpdate();
        _vars.Items.Clear();
        for (int g = 0; g < groups.Count; g++)
        {
            if (_tabMode == 1 && (g == 1 || g == 2 || g == 3)) continue;   // 内置: 除 HWiNFO 组
            if (_tabMode == 2 && g != 1 && g != 2 && g != 3) continue;      // HWiNFO: 温/扇/占
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

    /// <summary>模块变量取值(异常隔离: 任何异常显示 --, 不影响渲染)</summary>
    private static string SafeModuleValue(ModuleVar v)
    {
        try
        {
            string s = v.Value != null ? v.Value() : null;
            return string.IsNullOrEmpty(s) ? "--" : s;
        }
        catch { return "--"; }
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
        _varHost.Width = _varsCollapsed ? 40 : VarHostW;
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
        if (_warnUntil != 0 && Environment.TickCount > _warnUntil)
        {
            _warnUntil = 0;
            _warn.Visible = false;
        }
    }

    private void UpdateStatus()
    {
        _lhmStatus.ForeColor = _eng.LhmOk ? UiTheme.Good : UiTheme.Bad;
        _lhmStatus.Text = _eng.LhmOk ? "LHM ● CPU/SoC/内存/占用" : "LHM ○ 已切系统计数器兜底";
        if (_eng.HwStateText.StartsWith("HWiNFO ●")) _hwStatus.ForeColor = UiTheme.Good;
        else if (_eng.HwStateText.StartsWith("HWiNFO 已运行")) _hwStatus.ForeColor = UiTheme.Warn;
        else _hwStatus.ForeColor = UiTheme.TextDim;
        _hwStatus.Text = _eng.HwStateText;
        if (!_eng.HwStateText.StartsWith("HWiNFO ●")) _hwStatus.Text += " · 点击安装引导";
        else _hwStatus.Text += " · 数据已接通";

        // 模块状态: DSH ● / DeepSeek ●
        StringBuilder mb = new StringBuilder("模块: ");
        foreach (IModule m in _modules.Modules)
        {
            mb.Append(m.Name).Append(m.Ok ? " ● " : " ○ ").Append(m.StatusText).Append("  ");
        }
        _modStatus.Text = mb.ToString().TrimEnd();
        _modStatus.ForeColor = UiTheme.TextDim;
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
                    for (int i = 0; i < v.Length && i < _grpCollapsed.Length; i++)
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
            for (int i = 0; i < _grpCollapsed.Length; i++) gb.Append(_grpCollapsed[i] ? "1" : "0");
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
            System.Threading.Thread.Sleep(1500);   // 等扩展模块完成首轮轮询
            StringBuilder mods = new StringBuilder();
            foreach (IModule m in f._modules.Modules)
            {
                mods.Append("mod " + m.Id + " ok=" + m.Ok + " status=" + m.StatusText + "\n");
                foreach (ModuleVar mv in m.Variables())
                {
                    string val = "--";
                    try { val = mv.Value(); } catch { }
                    mods.Append("   {" + mv.Token + "} = " + val + (mv.Unit.Length > 0 ? " " + mv.Unit : "") + "\n");
                }
            }
            // 非 ASCII 过滤(1602 只支持 ASCII)
            bool removed;
            string filtered = FilterAsciiText("CPU 中文 06%", out removed);

            File.WriteAllText(Path.Combine(outDir, "selftest.txt"),
                "line0=[" + l0 + "]\nline1=[" + l1 + "]\nfan=" + f._eng.FanRpm + " temp=" + f._eng.CpuRealC + "\n"
                + "lhmOk=" + f._eng.LhmOk + " realCpu=" + f._eng.CpuPct + "% realRam=" + f._eng.RamPct
                + "% realSoc=" + f._eng.SocC + "C hw=" + f._eng.HwCount + "\n"
                + "bounds editor=" + f.GetEditorBounds() + " host=" + f.GetHostBounds()
                + " vars=" + f.GetVarsBounds() + " status=" + f.GetStatusBounds() + "\n"
                + "lcd px=" + f._lcd.PixelSize + " size=" + f._lcd.Width + "x" + f._lcd.Height
                + " hostH=" + f._host.Height + "\n"
                + "asciiFilter=[" + filtered + "] removed=" + removed + "\n"
                + mods.ToString());
            bool layoutOk = f._lcd.Width >= 700 && f._editor.Top >= f._host.Bottom;
            bool filterOk = removed && filtered == "CPU  06%";
            bool ok = l0.StartsWith("CPU 37%") && l1.Contains("56") && l1.Contains("hello")
                && f._eng.LhmOk && layoutOk && filterOk;
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
