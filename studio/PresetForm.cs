using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// 预设管理器: 保存/载入/删除整套模板(两行+滚动+背光)
internal class PresetForm : Form
{
    private readonly Action<string> _onLoad, _onSave, _onDelete;
    private readonly Func<List<string>> _list;
    private ListBox _listBox;
    private TextBox _name;
    private Label _msg;

    public PresetForm(Func<List<string>> list, Action<string> onLoad, Action<string> onSave, Action<string> onDelete)
    {
        _list = list;
        _onLoad = onLoad;
        _onSave = onSave;
        _onDelete = onDelete;

        Text = "预设管理";
        ClientSize = new Size(420, 340);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(27, 28, 32);
        ForeColor = Color.FromArgb(224, 226, 230);
        Font = new Font("Microsoft YaHei UI", 9f);

        Label t1 = new Label();
        t1.Text = "保存当前模板为预设 — 名称:";
        t1.Location = new Point(14, 12);
        t1.Size = new Size(220, 20);
        t1.ForeColor = Color.FromArgb(150, 153, 160);
        Controls.Add(t1);

        _name = new TextBox();
        _name.Location = new Point(14, 36);
        _name.Size = new Size(250, 26);
        _name.BackColor = Color.FromArgb(38, 40, 46);
        _name.ForeColor = Color.FromArgb(235, 238, 242);
        Controls.Add(_name);

        Button bSave = FlatBtn("保存 / 更新");
        bSave.Location = new Point(276, 36);
        bSave.Size = new Size(126, 26);
        bSave.Click += (s, e) =>
        {
            string n = _name.Text.Trim();
            if (n.Length == 0) { Msg("请输入名称"); return; }
            _onSave(n);
            RefreshList();
            Msg("已保存: " + n);
        };
        Controls.Add(bSave);

        _listBox = new ListBox();
        _listBox.Location = new Point(14, 74);
        _listBox.Size = new Size(388, 190);
        _listBox.BackColor = Color.FromArgb(30, 31, 36);
        _listBox.ForeColor = Color.FromArgb(220, 222, 226);
        _listBox.BorderStyle = BorderStyle.None;
        _listBox.SelectedIndexChanged += (s, e) =>
        {
            string n = _listBox.SelectedItem as string;
            if (n != null) _name.Text = n;
        };
        _listBox.DoubleClick += (s, e) => DoLoad();
        Controls.Add(_listBox);

        Button bLoad = FlatBtn("载入选中");
        bLoad.Location = new Point(14, 276);
        bLoad.Size = new Size(120, 30);
        bLoad.Click += (s, e) => DoLoad();
        Controls.Add(bLoad);

        Button bDel = FlatBtn("删除选中");
        bDel.Location = new Point(144, 276);
        bDel.Size = new Size(120, 30);
        bDel.Click += (s, e) =>
        {
            string n = _listBox.SelectedItem as string;
            if (n == null) { Msg("先选中一个预设"); return; }
            if (MessageBox.Show(this, "删除预设 \"" + n + "\"?", "预设管理", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _onDelete(n);
            RefreshList();
            Msg("已删除: " + n);
        };
        Controls.Add(bDel);

        _msg = new Label();
        _msg.Location = new Point(280, 282);
        _msg.Size = new Size(126, 24);
        _msg.ForeColor = Color.FromArgb(120, 210, 120);
        Controls.Add(_msg);

        RefreshList();
    }

    private void DoLoad()
    {
        string n = _listBox.SelectedItem as string;
        if (n == null) { Msg("先选中一个预设"); return; }
        _onLoad(n);
        Msg("已载入: " + n);
    }

    private void RefreshList()
    {
        _listBox.BeginUpdate();
        _listBox.Items.Clear();
        foreach (string s in _list())
            _listBox.Items.Add(s);
        _listBox.EndUpdate();
    }

    private void Msg(string m) { _msg.Text = m; }

    private static Button FlatBtn(string text)
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
