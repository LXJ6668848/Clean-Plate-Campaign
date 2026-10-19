using System.Drawing;
using System.Windows.Forms;

namespace GuangPanCheckin;

/// <summary>右下角小提醒卡片：不是全屏，不抢焦点，可拖动，120 秒未处理自动收起。</summary>
public sealed class ReminderForm : Form
{
    public event Action? AckCheckin;
    /// <summary>点「去打卡」：只是去看/去操作，不算打卡，也不消耗本轮提醒次数。</summary>
    public event Action? GoCheckin;
    public event Action? Snooze;
    public event Action? MuteToday;

    private readonly Label _title;
    private readonly Label _sub;
    private readonly Label _detail;
    private readonly System.Windows.Forms.Timer _autoClose;
    private bool _dragging;
    private Point _dragOffset;

    public ReminderForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Theme.Card;
        Size = new Size(430, 184);
        Padding = new Padding(0);

        var strip = new Panel { BackColor = Theme.Accent, Dock = DockStyle.Left, Width = 4 };

        _title = UI.Lbl("该打卡了", 13.5f, Theme.Text, true);
        _title.Location = new Point(24, 16);

        _sub = UI.Lbl("", 9.5f, Theme.Muted);
        _sub.Location = new Point(24, 48);
        _sub.MaximumSize = new Size(382, 0);

        _detail = UI.Lbl("", 9.5f, Theme.Warn);
        _detail.Location = new Point(24, 72);
        _detail.MaximumSize = new Size(382, 0);

        // 主操作：「去打卡」——去看图/去小程序操作，不写记录
        var btnGo = UI.Flat("去打卡", Theme.Accent, Theme.FromHex("#06281C"), 11.5f);
        btnGo.SetBounds(24, 106, 168, 38);
        btnGo.Click += (_, _) => { GoCheckin?.Invoke(); Close(); };

        // 次操作：确认已经打过了
        var btnAck = UI.Flat("我已打卡", Theme.Card2, Theme.Text, 10f);
        btnAck.SetBounds(204, 106, 124, 38);
        btnAck.FlatAppearance.BorderSize = 1;
        btnAck.FlatAppearance.BorderColor = Theme.Border;
        btnAck.Click += (_, _) => { AckCheckin?.Invoke(); Close(); };

        // AutoSize 的 Label 宽度带内边距，用 TextRenderer 实测再排，避免中间空一大块
        using var linkFont = Theme.F(9f);
        var wSnooze = TextRenderer.MeasureText("10 分钟后再提醒", linkFont).Width;
        var wDot = TextRenderer.MeasureText("·", linkFont).Width;

        var lnkSnooze = Link("10 分钟后再提醒", 24, 152);
        lnkSnooze.Click += (_, _) => { Snooze?.Invoke(); Close(); };

        var dot = UI.Lbl("·", 9f, Theme.FromHex("#4A5563"));
        dot.Location = new Point(24 + wSnooze + 6, 151);

        var lnkMute = Link("今天不再提醒", 24 + wSnooze + 6 + wDot + 6, 152);
        lnkMute.Click += (_, _) => { MuteToday?.Invoke(); Close(); };

        Controls.AddRange(new Control[] { strip, _title, _sub, _detail, btnGo, btnAck, lnkSnooze, dot, lnkMute });

        _autoClose = new System.Windows.Forms.Timer { Interval = 120_000 };
        _autoClose.Tick += (_, _) => { _autoClose.Stop(); Close(); };

        MouseDown += OnDragStart;
        MouseMove += OnDragMove;
        MouseUp += (_, _) => _dragging = false;
        foreach (Control c in Controls) c.MouseDown += OnDragStart;
    }

    private static Label Link(string text, int x, int y)
    {
        var l = UI.Lbl(text, 9f, Theme.Muted);
        l.Location = new Point(x, y);
        l.Cursor = Cursors.Hand;
        l.MouseEnter += (_, _) => l.ForeColor = Theme.Text;
        l.MouseLeave += (_, _) => l.ForeColor = Theme.Muted;
        return l;
    }

    private void OnDragStart(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        var p = PointToClient(Cursor.Position);
        _dragOffset = new Point(p.X - Left, p.Y - Top);
    }

    private void OnDragMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = Cursor.Position;
        Location = new Point(p.X - _dragOffset.X, p.Y - _dragOffset.Y);
    }

    public void Present(int stage, DayState st, AppSettings s)
    {
        _title.Text = stage switch
        {
            0 => "该打卡了",
            1 => "还差一次打卡",
            _ => "最后提醒一次",
        };
        _sub.Text = st.Remaining > 1
            ? $"今天已完成 {st.Done}/{s.DailyLimit} 次，这次打完还有 {st.Remaining - 1} 次。"
            : $"今天已完成 {st.Done}/{s.DailyLimit} 次，打完这次今天就满了。";

        var extra = st.LatestNeeded is not null && st.Remaining > 0
            ? $"　最早可打 {Logic.Fmt(st.NextEarliest)}　最晚 {Logic.FmtDeadline(st.LatestNeeded, st.Date)}"
            : "";
        _detail.Text = $"第 {Math.Min(st.Done + 1, s.DailyLimit)} 次 · 间隔 {s.MinIntervalMinutes} 分钟" + extra;

        ApplyCorners();
        PlaceBottomRight();
        if (!Visible) Show();
        BringToFront();
        _autoClose.Stop();
        _autoClose.Start();
    }

    private void ApplyCorners()
    {
        using var path = RoundPanel.Round(new Rectangle(0, 0, Width, Height), 14);
        Region = new Region(path);
    }

    private void PlaceBottomRight()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW：不进 Alt+Tab
            return cp;
        }
    }
}
