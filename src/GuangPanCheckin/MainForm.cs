using System.Drawing;
using System.Windows.Forms;

namespace GuangPanCheckin;

public sealed class MainForm : Form
{
    private readonly TrayContext? _host;

    private readonly Label _lblProgress;
    private readonly Label _sub;
    private readonly Label _lblDots;
    private readonly Label _lblNextTime;
    private readonly Label _lblCountdown;
    private readonly Label _lblDeadline;
    private readonly Label _lblHint;
    private readonly Button _btnCheckin;
    private readonly Button _btnUndo;
    private readonly FlowLayoutPanel _history;

    private readonly System.Windows.Forms.Timer _ui;
    private string _historySignature = "";

    public MainForm(TrayContext? host)
    {
        _host = host;
        Text = "光盘打卡提醒";
        UI.DarkTitleBar(this);
        ClientSize = new Size(470, 716);
        ShowInTaskbar = true;
        DoubleBuffered = true;

        var title = UI.Lbl("光盘打卡提醒", 15f, Theme.Text, true);
        title.Location = new Point(20, 16);

        _sub = UI.Lbl("", 9f, Theme.Muted);
        _sub.Location = new Point(20, 46);

        // 进度卡片
        var card1 = new RoundPanel { Location = new Point(20, 78), Size = new Size(430, 132) };
        var cap1 = UI.Lbl("今日进度", 9f, Theme.Muted);
        cap1.Location = new Point(20, 14);
        cap1.BackColor = Theme.Card;
        _lblProgress = UI.Lbl("0 / 3", 26f, Theme.Text, true);
        _lblProgress.Location = new Point(16, 34);
        _lblProgress.BackColor = Theme.Card;
        _lblDots = UI.Lbl("", 15f, Theme.Accent);
        _lblDots.Location = new Point(20, 96);
        _lblDots.BackColor = Theme.Card;
        card1.Controls.AddRange(new Control[] { cap1, _lblProgress, _lblDots });

        // 下次可打卡卡片
        var card2 = new RoundPanel { Location = new Point(20, 222), Size = new Size(430, 108) };
        var cap2 = UI.Lbl("下次最早可打卡", 9f, Theme.Muted);
        cap2.Location = new Point(20, 14);
        cap2.BackColor = Theme.Card;
        _lblNextTime = UI.Lbl("--:--", 20f, Theme.Accent, true);
        _lblNextTime.Location = new Point(16, 34);
        _lblNextTime.BackColor = Theme.Card;
        _lblCountdown = UI.Lbl("", 9.5f, Theme.Muted);
        _lblCountdown.Location = new Point(20, 76);
        _lblCountdown.BackColor = Theme.Card;
        card2.Controls.AddRange(new Control[] { cap2, _lblNextTime, _lblCountdown });

        // 安全线
        var card3 = new RoundPanel { Location = new Point(20, 342), Size = new Size(430, 62), Radius = 12 };
        _lblDeadline = UI.Lbl("", 9.5f, Theme.Warn);
        _lblDeadline.Location = new Point(20, 22);
        _lblDeadline.BackColor = Theme.Card;
        card3.Controls.Add(_lblDeadline);

        _lblHint = UI.Lbl("", 9f, Theme.Danger);
        _lblHint.Location = new Point(20, 412);

        _btnCheckin = UI.Flat("我已打卡", Theme.Accent, Theme.FromHex("#06281C"), 12f);
        _btnCheckin.SetBounds(20, 434, 430, 56);
        _btnCheckin.Click += (_, _) => OnCheckin();

        _btnUndo = UI.Flat("撤销上一条", Theme.Card2, Theme.Muted, 9f);
        _btnUndo.SetBounds(20, 500, 138, 38);
        _btnUndo.Click += (_, _) => OnUndo();

        var btnSettings = UI.Flat("设置", Theme.Card2, Theme.Text, 9f);
        btnSettings.SetBounds(166, 500, 138, 38);
        btnSettings.Click += (_, _) => _host?.ShowSettings(this);

        var btnPhone = UI.Flat("同步到手机", Theme.Card2, Theme.Text, 9f);
        btnPhone.SetBounds(312, 500, 138, 38);
        btnPhone.Click += (_, _) => _host?.ExportToPhone(true);

        var capHist = UI.Lbl("最近 5 天", 9f, Theme.Muted);
        capHist.Location = new Point(20, 552);

        _history = new FlowLayoutPanel
        {
            Location = new Point(20, 578),
            Size = new Size(430, 120),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = false,
            BackColor = Theme.Card,
            Padding = new Padding(0)
        };

        Controls.AddRange(new Control[]
        {
            title, _sub, card1, card2, card3, _lblHint,
            _btnCheckin, _btnUndo, btnSettings, btnPhone, capHist, _history
        });

        _ui = new System.Windows.Forms.Timer { Interval = 1000 };
        _ui.Tick += (_, _) => RefreshState();
        _ui.Start();

        RefreshState();
    }

    public void RefreshState()
    {
        Store.NormalizeRuntime();
        var s = Store.Settings;
        var rt = Store.Runtime;
        var now = DateTimeOffset.Now;
        var st = Logic.Compute(s, Store.Records, now);
        var due = Schedule.CurrentDue(s, st, rt);

        _sub.Text = rt.DayStart is { } dayStart
            ? $"每日 {s.DailyLimit} 次 · 间隔 {s.MinIntervalMinutes} 分钟 · 今天从 {dayStart:HH:mm} 开始计时"
            : $"每日 {s.DailyLimit} 次 · 间隔 {s.MinIntervalMinutes} 分钟 · 等你第一次操作电脑后开始计时";

        _lblProgress.Text = $"{st.Done} / {s.DailyLimit}";

        var dots = "";
        for (var i = 0; i < s.DailyLimit; i++)
            dots += i < st.Done ? "● " : "○ ";
        _lblDots.Text = dots.TrimEnd();
        _lblDots.ForeColor = st.IsFinished ? Theme.Accent : Theme.Muted;

        if (st.IsFinished)
        {
            _lblNextTime.Text = "今日已完成";
            _lblNextTime.ForeColor = Theme.Accent;
            _lblCountdown.Text = "明天 0 点自动刷新，可以休息了。";
            _btnCheckin.Text = "今天已打满";
            _btnCheckin.BackColor = Theme.Card2;
            _btnCheckin.ForeColor = Theme.Muted;
        }
        else if (st.CanCheckNow)
        {
            _lblNextTime.Text = "现在可以打卡";
            _lblNextTime.ForeColor = Theme.Accent;
            _lblCountdown.Text = st.LastCheckin is null
                ? (rt.DayStart is { } d0
                    ? $"今天从 {d0:HH:mm} 开始计时，先打一次吧。"
                    : "今天还没检测到你在用电脑，动一下鼠标就开始计时。")
                : $"上次打卡 {st.LastCheckin:HH:mm}，间隔已满 {s.MinIntervalMinutes} 分钟。";
            _btnCheckin.Text = "我已打卡";
            _btnCheckin.BackColor = Theme.Accent;
            _btnCheckin.ForeColor = Theme.FromHex("#06281C");
        }
        else
        {
            _lblNextTime.Text = Logic.Fmt(st.NextEarliest);
            _lblNextTime.ForeColor = Theme.Warn;
            var wait = st.NextEarliest!.Value - now;
            _lblCountdown.Text = $"还需等待 {Logic.Humanize(wait)}" +
                (st.LastCheckin is not null ? $"（上次 {st.LastCheckin:HH:mm}）" : "") +
                (due is not null ? $" · 下次提醒 {Logic.Fmt(due)}" : "");
            _btnCheckin.Text = $"{Logic.Fmt(st.NextEarliest)} 后可打卡";
            _btnCheckin.BackColor = Theme.Card2;
            _btnCheckin.ForeColor = Theme.Muted;
        }

        if (rt.WaitingForActivity && !st.IsFinished)
        {
            _lblCountdown.Text = $"已到提醒时间（{Logic.Fmt(due)}），动一下鼠标或键盘就弹";
            _lblCountdown.ForeColor = Theme.Warn;
        }
        else
        {
            _lblCountdown.ForeColor = Theme.Muted;
        }

        if (st.IsFinished)
        {
            _lblDeadline.Text = "今天 3 次都完成了，明天继续保持。";
            _lblDeadline.ForeColor = Theme.Muted;
        }
        else if (st.MaxAchievable < st.Remaining)
        {
            _lblDeadline.Text = $"今天最多还能完成 {st.MaxAchievable} 次（原计划 {st.Remaining} 次），最晚 {Logic.FmtDeadline(st.LatestNeeded, st.Date)} 要打下一次。";
            _lblDeadline.ForeColor = Theme.Danger;
        }
        else if (st.Remaining == 1)
        {
            _lblDeadline.Text = $"今天还剩 1 次，{Logic.FmtDeadline(st.LatestNeeded, st.Date)} 前打完就行。";
            _lblDeadline.ForeColor = Theme.Muted;
        }
        else
        {
            _lblDeadline.Text = $"最晚 {Logic.FmtDeadline(st.LatestNeeded, st.Date)} 前打下一次，才来得及完成今天的 {st.Remaining} 次。";
            _lblDeadline.ForeColor = Theme.Warn;
        }

        var canUndo = TryGetUndoable(out var rec);
        _btnUndo.Enabled = canUndo;
        _btnUndo.ForeColor = canUndo ? Theme.Text : Theme.FromHex("#4A5563");
        _btnUndo.Text = canUndo ? $"撤销 {rec!.Ts:HH:mm} 那条" : "撤销上一条";

        RebuildHistory();
    }

    private static bool TryGetUndoable(out CheckRecord? rec)
    {
        rec = null;
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var last = Store.Records.Where(r => r.Date == today).OrderBy(r => r.Ts).LastOrDefault();
        if (last is null) return false;
        if (DateTimeOffset.Now - last.Ts > TimeSpan.FromMinutes(5)) return false;
        rec = last;
        return true;
    }

    private void RebuildHistory()
    {
        var today = DateTime.Today;
        var days = Enumerable.Range(0, 5).Select(i => today.AddDays(-i)).ToList();
        var sig = string.Join(",", days.Select(d => d.ToString("yyyy-MM-dd"))) + "|" + Store.Records.Count;
        if (sig == _historySignature) return;
        _historySignature = sig;

        _history.SuspendLayout();
        _history.Controls.Clear();

        foreach (var day in days)
        {
            var key = day.ToString("yyyy-MM-dd");
            var recs = Store.Records.Where(r => r.Date == key).OrderBy(r => r.Ts).ToList();
            var row = new Panel { Width = 424, Height = 24, BackColor = Theme.Card };

            var dateText = day == today ? "今天" : day.ToString("MM-dd ddd");
            var dl = UI.Lbl(dateText, 9f, day == today ? Theme.Text : Theme.Muted);
            dl.Location = new Point(2, 3);
            dl.BackColor = Theme.Card;

            var cnt = UI.Lbl($"{recs.Count}/{Store.Settings.DailyLimit}", 9f,
                recs.Count >= Store.Settings.DailyLimit ? Theme.Accent : Theme.Muted);
            cnt.Location = new Point(150, 3);
            cnt.BackColor = Theme.Card;

            var times = recs.Count == 0 ? "无记录" : string.Join("  ", recs.Select(r => r.Ts.ToString("HH:mm")));
            var tl = UI.Lbl(times, 9f, recs.Count == 0 ? Theme.FromHex("#55616E") : Theme.Muted);
            tl.Location = new Point(200, 3);
            tl.BackColor = Theme.Card;

            row.Controls.AddRange(new Control[] { dl, cnt, tl });
            _history.Controls.Add(row);
        }

        _history.ResumeLayout();
    }

    private void OnCheckin()
    {
        var (ok, msg) = _host?.TryCheckin() ?? (false, "程序未初始化");
        _historySignature = "";
        RefreshState();
        if (!ok)
        {
            _lblHint.ForeColor = Theme.Danger;
            _lblHint.Text = msg;
            System.Media.SystemSounds.Hand.Play();
        }
        else
        {
            _lblHint.ForeColor = Theme.Accent;
            _lblHint.Text = $"已记录 {DateTime.Now:HH:mm}，下次最早 {Logic.Fmt(Logic.Compute(Store.Settings, Store.Records, DateTimeOffset.Now).NextEarliest)}。";
        }
    }

    private void OnUndo()
    {
        if (!TryGetUndoable(out var rec) || rec is null) return;
        if (MessageBox.Show(this, $"撤销 {rec.Ts:HH:mm} 的打卡记录？", "确认撤销",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        Store.Records.RemoveAll(r => r.Id == rec.Id);
        Store.SaveRecords();
        AppLog.Info($"撤销记录 {rec.Ts:yyyy-MM-dd HH:mm}");
        _historySignature = "";
        RefreshState();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 关窗 = 收进托盘，真正退出走托盘菜单
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _host?.NotifyHiddenToTray();
            return;
        }
        base.OnFormClosing(e);
    }
}
