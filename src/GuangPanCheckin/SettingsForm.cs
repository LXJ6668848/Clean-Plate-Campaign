using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace GuangPanCheckin;

public sealed class SettingsForm : Form
{
    private readonly TrayContext? _host;

    private readonly NumericUpDown _limit = new();
    private readonly NumericUpDown _interval = new();
    private readonly NumericUpDown _idle = new();
    private readonly TextBox[] _anchors = new TextBox[5];
    private readonly TextBox _quietStart = new();
    private readonly TextBox _quietEnd = new();
    private readonly NumericUpDown _esc1 = new();
    private readonly NumericUpDown _esc2 = new();
    private readonly CheckBox _sound = new();
    private readonly CheckBox _popup = new();
    private readonly CheckBox _balloon = new();
    private readonly CheckBox _crossDay = new();
    private readonly CheckBox _autoStart = new();
    private Label? _imgState;

    public SettingsForm(TrayContext? host)
    {
        _host = host;
        var s = Store.Settings;

        Text = "设置";
        UI.DarkTitleBar(this);
        ClientSize = new Size(520, 864);
        DoubleBuffered = true;

        var y = 24;
        var title = UI.Lbl("设置", 14f, Theme.Text, true);
        title.Location = new Point(24, y);
        Controls.Add(title);
        y += 44;

        AddRow("每日次数上限", _limit, ref y);
        _limit.Minimum = 1;
        _limit.Maximum = 5;
        _limit.Value = Math.Clamp(s.DailyLimit, 1, 5);

        AddRow("最小间隔（分钟）", _interval, ref y);
        _interval.Minimum = 30;
        _interval.Maximum = 360;
        _interval.Increment = 10;
        _interval.Value = Math.Clamp(s.MinIntervalMinutes, 30, 360);

        AddRow("有操作判定（空闲秒数）", _idle, ref y);
        _idle.Minimum = 5;
        _idle.Maximum = 600;
        _idle.Increment = 5;
        _idle.Value = Math.Clamp(s.ActiveIdleSeconds, 5, 600);

        var capAnchor = UI.Lbl("手机日历兜底时刻（HH:mm，留空即不用；不影响电脑弹窗）", 9f, Theme.Muted);
        capAnchor.Location = new Point(24, y);
        Controls.Add(capAnchor);
        y += 26;

        for (var i = 0; i < _anchors.Length; i++)
        {
            var tb = new TextBox
            {
                Location = new Point(190, y),
                Width = 110,
                BackColor = Theme.Card2,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.F(10f),
                Text = i < s.Anchors.Count ? s.Anchors[i] : ""
            };
            _anchors[i] = tb;
            var lbl = UI.Lbl($"兜底时刻 {i + 1}", 9f, Theme.Muted);
            lbl.Location = new Point(24, y + 4);
            Controls.Add(lbl);
            Controls.Add(tb);
            y += 38;
        }

        var capBulk = UI.Lbl("电脑端不必填这里：它按“首次操作 + 间隔”自己算。手机端只能按固定时刻响。", 8.5f, Theme.FromHex("#5C6875"));
        capBulk.Location = new Point(24, y);
        Controls.Add(capBulk);
        y += 24;

        y += 6;
        var capQuiet = UI.Lbl("免打扰时段", 9f, Theme.Muted);
        capQuiet.Location = new Point(24, y + 6);
        Controls.Add(capQuiet);
        _quietStart.Text = s.QuietStart;
        _quietStart.SetBounds(190, y, 80, 26);
        StyleBox(_quietStart);
        var dash = UI.Lbl("至", 9f, Theme.Muted);
        dash.Location = new Point(280, y + 4);
        _quietEnd.Text = s.QuietEnd;
        _quietEnd.SetBounds(302, y, 80, 26);
        StyleBox(_quietEnd);
        Controls.AddRange(new Control[] { _quietStart, dash, _quietEnd });
        y += 42;

        // 打卡示意图（点「去打卡」时展示）
        var capImg = UI.Lbl("打卡示意图", 9f, Theme.Muted);
        capImg.Location = new Point(24, y + 6);
        var btnImg = UI.Flat("更换…", Theme.Card2, Theme.Text, 9f);
        btnImg.SetBounds(190, y, 88, 30);
        btnImg.Click += (_, _) => PickImage();
        var btnImgDir = UI.Flat("打开目录", Theme.Card2, Theme.Muted, 9f);
        btnImgDir.SetBounds(288, y, 92, 30);
        btnImgDir.Click += (_, _) => OpenAssetDir();
        Controls.AddRange(new Control[] { capImg, btnImg, btnImgDir });
        y += 36;

        _imgState = UI.Lbl("", 8.5f, Theme.FromHex("#5C6875"));
        _imgState.Location = new Point(190, y);
        Controls.Add(_imgState);
        y += 26;

        var capEsc = UI.Lbl("未确认时升级提醒", 9f, Theme.Muted);
        capEsc.Location = new Point(24, y + 6);
        Controls.Add(capEsc);
        _esc1.Minimum = 1; _esc1.Maximum = 60; _esc1.Value = Math.Clamp(s.Escalate1Minutes, 1, 60);
        _esc1.SetBounds(190, y, 70, 26);
        var l1 = UI.Lbl("分钟后，再提醒一次", 9f, Theme.Muted);
        l1.Location = new Point(266, y + 4);
        Controls.AddRange(new Control[] { _esc1, l1 });
        y += 32;

        _esc2.Minimum = 5; _esc2.Maximum = 120; _esc2.Value = Math.Clamp(s.Escalate2Minutes, 5, 120);
        _esc2.SetBounds(190, y, 70, 26);
        var l2 = UI.Lbl("分钟后，最后一次提醒（之后本轮不再打扰）", 9f, Theme.Muted);
        l2.Location = new Point(266, y + 4);
        Controls.AddRange(new Control[] { _esc2, l2 });
        y += 40;

        AddCheck("提醒时播放提示音", _sound, s.SoundEnabled, ref y);        AddCheck("弹出右下角提醒卡片", _popup, s.PopupEnabled, ref y);
        AddCheck("同时发托盘气泡通知", _balloon, s.BalloonEnabled, ref y);
        AddCheck("跨天也受最小间隔限制（23:50 打过，次日 00:10 不能打）", _crossDay, s.IntervalAcrossDays, ref y);
        AddCheck("开机自动启动", _autoStart, s.AutoStart, ref y);

        y += 10;
        var dir = UI.Lbl("数据目录：" + Store.Dir, 8.5f, Theme.FromHex("#5C6875"));
        dir.Location = new Point(24, y);
        Controls.Add(dir);

        var btnSave = UI.Flat("保存", Theme.Accent, Theme.FromHex("#06281C"), 10f);
        btnSave.SetBounds(24, 786, 220, 44);
        btnSave.Click += (_, _) => Save();

        var btnCancel = UI.Flat("取消", Theme.Card2, Theme.Text, 10f);
        btnCancel.SetBounds(262, 786, 220, 44);
        btnCancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { btnSave, btnCancel });

        RefreshImageState();
    }

    private void RefreshImageState()
    {
        if (_imgState is null) return;
        var desc = AssetStore.Describe();
        _imgState.Text = desc;
        _imgState.ForeColor = desc.StartsWith("已设置") ? Theme.FromHex("#5C6875") : Theme.Warn;
    }

    private void PickImage()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择打卡示意图",
            Filter = "图片 (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var (ok, msg) = AssetStore.Import(dlg.FileName);
        if (!ok)
        {
            MessageBox.Show(this, msg, "换图失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        RefreshImageState();
    }

    private void OpenAssetDir()
    {
        try
        {
            Directory.CreateDirectory(AssetStore.Dir);
            Process.Start("explorer.exe", $"\"{AssetStore.Dir}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打不开目录：" + ex.Message, "出错了",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void StyleBox(TextBox tb)
    {
        tb.BackColor = Theme.Card2;
        tb.ForeColor = Theme.Text;
        tb.BorderStyle = BorderStyle.FixedSingle;
        tb.Font = Theme.F(10f);
    }

    private void AddRow(string text, NumericUpDown box, ref int y)
    {
        var lbl = UI.Lbl(text, 9f, Theme.Muted);
        lbl.Location = new Point(24, y + 4);
        box.SetBounds(190, y, 90, 26);
        box.BackColor = Theme.Card2;
        box.ForeColor = Theme.Text;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = Theme.F(10f);
        Controls.Add(lbl);
        Controls.Add(box);
        y += 38;
    }

    private void AddCheck(string text, CheckBox cb, bool value, ref int y)
    {
        cb.Text = text;
        cb.Checked = value;
        cb.ForeColor = Theme.Text;
        cb.Font = Theme.F(9.5f);
        cb.AutoSize = true;
        cb.Location = new Point(24, y);
        Controls.Add(cb);
        y += 26;
    }

    private void Save()
    {
        // 兜底时刻与次数无关：填几个就导几个，留空的行直接忽略
        var anchors = new List<(TimeSpan T, string Raw)>();
        for (var i = 0; i < _anchors.Length; i++)
        {
            var raw = _anchors[i].Text.Trim();
            if (raw.Length == 0) continue;
            var t = Logic.ParseTime(raw);
            if (t is null)
            {
                MessageBox.Show(this, $"第 {i + 1} 行的时刻格式不对，请用 HH:mm，例如 08:00。",
                    "格式错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _anchors[i].Focus();
                return;
            }
            anchors.Add((t.Value, raw));
        }

        if (anchors.Count == 0)
        {
            MessageBox.Show(this, "至少填一个兜底时刻，否则导出到手机日历里不会有任何提醒。",
                "还差一项", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _anchors[0].Focus();
            return;
        }

        if (anchors.Count > IcsExporter.MaxPerDay)
        {
            MessageBox.Show(this, $"最多 {IcsExporter.MaxPerDay} 个兜底时刻，太多反而会被自己忽略。",
                "太多了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (Logic.ParseTime(_quietStart.Text) is null || Logic.ParseTime(_quietEnd.Text) is null)
        {
            MessageBox.Show(this, "免打扰时段格式不对，请用 HH:mm。", "格式错误",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var s = Store.Settings;
        s.DailyLimit = (int)_limit.Value;
        s.MinIntervalMinutes = (int)_interval.Value;
        s.Anchors = anchors
            .OrderBy(a => a.T)
            .Select(a => $"{a.T.Hours:00}:{a.T.Minutes:00}")
            .Distinct()
            .ToList();
        s.QuietStart = _quietStart.Text.Trim();
        s.QuietEnd = _quietEnd.Text.Trim();
        s.Escalate1Minutes = (int)_esc1.Value;
        s.Escalate2Minutes = (int)_esc2.Value;
        s.SoundEnabled = _sound.Checked;
        s.PopupEnabled = _popup.Checked;
        s.BalloonEnabled = _balloon.Checked;
        s.IntervalAcrossDays = _crossDay.Checked;
        s.AutoStart = _autoStart.Checked;
        s.ActiveIdleSeconds = (int)_idle.Value;

        Store.SaveSettings();
        TrayContext.ApplyAutoStart(s.AutoStart);
        _host?.ResetReminderRound();
        _host?.RefreshUi();
        AppLog.Info("设置已保存");
        Close();
    }
}
