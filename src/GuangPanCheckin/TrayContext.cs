using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GuangPanCheckin;

public static class IconFactory
{
    public static Icon Create(string state)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var color = state switch
            {
                "done" => Theme.Accent,
                "action" => Theme.Warn,
                _ => Theme.FromHex("#7F8C99")
            };

            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 29, 29);
            using var pen = new Pen(Theme.Bg, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(9f, 16.5f), new PointF(13.5f, 21.5f), new PointF(23f, 10.5f)
            });
        }

        return Icon.FromHandle(bmp.GetHicon());
    }
}

public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _engine;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _miStatus;
    private readonly ToolStripMenuItem _miMute;
    private readonly ToolStripMenuItem _miAutoRun;

    /// <summary>不可见的锚点窗体：只为了让后台线程有一个「已创建句柄」的 UI 线程入口。</summary>
    private readonly Form _anchor;

    private MainForm? _main;
    private SettingsForm? _settings;
    private ReminderForm? _popup;
    private ImageForm? _imageForm;
    private string _iconState = "";
    private EventWaitHandle? _showEvent;
    private volatile bool _disposed;

    /// <summary>上一次 tick 的时间，用来识别睡眠/休眠造成的时间跳变（只在内存里，不落盘）。</summary>
    private DateTimeOffset _lastTick;

    /// <summary>屏幕是否锁着。锁屏期间一律视为“没人在操作”。</summary>
    private volatile bool _locked;

    public TrayContext()
    {
        Store.Load();
        if (Store.Settings.AutoStart) ApplyAutoStart(true);

        _anchor = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
            Size = new Size(1, 1),
            Opacity = 0
        };
        _ = _anchor.Handle; // 强制创建句柄

        _miStatus = new ToolStripMenuItem("正在统计…") { Enabled = false };
        var miCheckin = new ToolStripMenuItem("立即打卡（记录一次）");
        miCheckin.Click += (_, _) => TryCheckin();

        var miOpen = new ToolStripMenuItem("打开面板");
        miOpen.Click += (_, _) => ShowMain();

        var miImage = new ToolStripMenuItem("查看打卡示意图");
        miImage.Click += (_, _) => ShowImageCard();

        var miPreview = new ToolStripMenuItem("预览提醒卡片");
        miPreview.Click += (_, _) => PreviewReminder();

        var miSettings = new ToolStripMenuItem("设置…");
        miSettings.Click += (_, _) => ShowSettings(null);

        var miPhone = new ToolStripMenuItem("同步到手机日历…");
        miPhone.Click += (_, _) => ExportToPhone(true);

        _miMute = new ToolStripMenuItem("今天不再提醒") { CheckOnClick = true };
        _miMute.Click += (_, _) =>
        {
            Store.Runtime.MutedToday = _miMute.Checked;
            Store.SaveRuntime();
            RefreshUi();
        };

        _miAutoRun = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true, Checked = Store.Settings.AutoStart };
        _miAutoRun.Click += (_, _) =>
        {
            Store.Settings.AutoStart = _miAutoRun.Checked;
            Store.SaveSettings();
            ApplyAutoStart(_miAutoRun.Checked);
        };

        var miExit = new ToolStripMenuItem("退出");
        miExit.Click += (_, _) => ExitApp();

        _menu = new ContextMenuStrip { BackColor = Theme.Card, ForeColor = Theme.Text };
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _miStatus,
            new ToolStripSeparator(),
            miCheckin,
            miOpen,
            miImage,
            miPreview,
            miSettings,
            miPhone,
            new ToolStripSeparator(),
            _miMute,
            _miAutoRun,
            new ToolStripSeparator(),
            miExit
        });
        foreach (ToolStripItem it in _menu.Items) it.ForeColor = Theme.Text;

        _tray = new NotifyIcon
        {
            Icon = IconFactory.Create("wait"),
            Visible = true,
            Text = "光盘打卡提醒",
            ContextMenuStrip = _menu
        };
        _tray.DoubleClick += (_, _) => ShowMain();

        _engine = new System.Windows.Forms.Timer { Interval = 10_000 };
        _engine.Tick += (_, _) => EngineTick();
        _engine.Start();

        HookSystemEvents();
        StartShowListener();
        EngineTick();
        RefreshUi();
    }

    /// <summary>锁屏/解锁、睡眠恢复都要立刻重算一次，不能等下一个 10 秒。</summary>
    private void HookSystemEvents()
    {
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionEnding += (_, _) => Store.SaveRuntime();
        }
        catch (Exception ex)
        {
            AppLog.Warn("挂接系统事件失败: " + ex.Message);
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        try
        {
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                    _locked = true;
                    AppLog.Info("屏幕已锁定：暂停弹窗（但计时继续）");
                    break;

                case SessionSwitchReason.SessionUnlock:
                    _locked = false;
                    AppLog.Info("屏幕已解锁：恢复检测");
                    Tick();
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("处理锁屏事件失败: " + ex.Message);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        AppLog.Info("系统从睡眠/休眠恢复：立刻重算一次");
        _lastTick = default; // 让下一次 tick 自己判断跳变
        Tick();
    }

    /// <summary>系统事件回调不在 UI 线程，必须切回 UI 线程再改状态/弹窗。</summary>
    private void Tick()
    {
        try
        {
            if (_disposed) return;
            _anchor.BeginInvoke(new Action(EngineTick));
        }
        catch (Exception ex)
        {
            AppLog.Warn("调度 tick 失败: " + ex.Message);
        }
    }

    // ---------- 提醒引擎 ----------

    private void EngineTick()
    {
        try
        {
            var now = DateTimeOffset.Now;
            var s = Store.Settings;
            var rt = Store.Runtime;

            // 定时器在睡眠/休眠期间是停的，回来看这一跳有多大就知道中间断没断
            if (_lastTick != default)
            {
                var gap = (now - _lastTick).TotalSeconds;
                if (gap > 90) AppLog.Info($"时间跳变 {(int)gap}s：可能刚从睡眠/休眠恢复");
            }
            _lastTick = now;

            // 跨天：清空计时起点与轮次
            Store.NormalizeRuntime();

            // 时间被往回调（改系统时间/时区）时，把起点拉回现在，避免负数倒计时
            if (rt.DayStart is { } ds && now < ds)
            {
                AppLog.Warn($"检测到时间倒退，重置计时起点（原 {ds:HH:mm:ss}）");
                rt.DayStart = now;
                rt.NextDue = Schedule.FirstDue(now, s);
                rt.RoundFiredAt = null;
                rt.RoundStage = -1;
                rt.WaitingForActivity = false;
                Store.SaveRuntime();
            }

            var idle = Activity.IdleSeconds();
            var active = !_locked && Schedule.IsActive(s, idle);

            // 计时起点：每天只在第一次检测到操作时初始化一次
            if (rt.DayStart is null && active)
            {
                rt.DayStart = now;
                rt.NextDue = Schedule.FirstDue(now, s);
                rt.RoundFiredAt = null;
                rt.RoundStage = -1;
                rt.WaitingForActivity = false;
                Store.SaveRuntime();
                AppLog.Info($"今日计时起点 {now:HH:mm:ss}（首次检测到操作），首轮提醒 {rt.NextDue:HH:mm}");
            }

            var st = Logic.Compute(s, Store.Records, now);
            RefreshUi();

            var plan = Schedule.Plan(s, st, rt, now, idle, _locked);
            switch (plan.Action)
            {
                case RemindAction.Fire:
                    Fire(plan.Stage, st, s);
                    if (plan.Stage == 0)
                    {
                        rt.RoundFiredAt = now;
                        rt.RoundStage = 0;
                        rt.NextDue = now.AddMinutes(Math.Max(1, s.MinIntervalMinutes));
                    }
                    else
                    {
                        rt.RoundStage = plan.Stage;
                    }
                    rt.WaitingForActivity = false;
                    Store.SaveRuntime();
                    AppLog.Info($"提醒 stage={plan.Stage}（{plan.Reason}），下一轮 {rt.NextDue:HH:mm}");
                    break;

                case RemindAction.Deferred:
                    if (!rt.WaitingForActivity)
                    {
                        rt.WaitingForActivity = true;
                        Store.SaveRuntime();
                        AppLog.Info($"到点但没人在操作，先压着不弹（{plan.Reason}）");
                    }
                    break;

                default:
                    if (rt.WaitingForActivity)
                    {
                        rt.WaitingForActivity = false;
                        Store.SaveRuntime();
                        AppLog.Info("已恢复到点前的等待状态");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("EngineTick 异常: " + ex);
        }
    }

    private void Fire(int stage, DayState st, AppSettings s)
    {
        if (s.PopupEnabled)
        {
            EnsurePopup();
            _popup!.Present(stage, st, s);
        }

        var title = stage switch
        {
            0 => "该打卡了",
            1 => "还没打卡，提醒第二次",
            _ => "最后一次提醒"
        };
        var body = $"今天 {st.Done}/{s.DailyLimit} 次｜最早可打 {Logic.Fmt(st.NextEarliest)}";

        if (s.BalloonEnabled)
        {
            try { _tray.ShowBalloonTip(8000, title, body, ToolTipIcon.Info); } catch { }
        }

        if (s.SoundEnabled)
        {
            System.Media.SystemSounds.Exclamation.Play();
        }
    }

    private void EnsurePopup()
    {
        if (_popup is { IsDisposed: false }) return;

        _popup = new ReminderForm();
        _popup.AckCheckin += () => TryCheckin();

        // 「去打卡」：卡片自己会关，这里负责把示意图亮出来
        _popup.GoCheckin += () =>
        {
            AppLog.Info("用户点击「去打卡」");
            ShowImageCard();
        };

        _popup.Snooze += () =>
        {
            Store.Runtime.SnoozeUntil = DateTimeOffset.Now.AddMinutes(10);
            Store.SaveRuntime();
            AppLog.Info("用户选择 10 分钟后再提醒");
        };
        _popup.MuteToday += () =>
        {
            Store.Runtime.MutedToday = true;
            Store.SaveRuntime();
            RefreshUi();
        };
    }

    /// <summary>预览提醒卡片：不写提醒标记，不改任何状态，纯粹给你看版式。</summary>
    public void PreviewReminder()
    {
        try
        {
            var st = Logic.Compute(Store.Settings, Store.Records, DateTimeOffset.Now);
            EnsurePopup();
            _popup!.Present(0, st, Store.Settings);
        }
        catch (Exception ex)
        {
            AppLog.Warn("预览提醒失败: " + ex.Message);
        }
    }

    /// <summary>打开「去打卡」示意图。已在显示时只置顶，不重复开窗。</summary>
    public void ShowImageCard()
    {
        try
        {
            if (_imageForm is { IsDisposed: false })
            {
                _imageForm.Nudge();
                return;
            }

            var (img, err) = AssetStore.Load();
            if (img is null)
            {
                AppLog.Warn("示意图不可用: " + err);
                try
                {
                    _tray.ShowBalloonTip(6000, "示意图打不开",
                        err + "。右键托盘图标 → 设置，可以重新选一张。", ToolTipIcon.Warning);
                }
                catch { }
                return;
            }

            var f = new ImageForm(img);
            f.AckCheckin += () =>
            {
                var (ok, _) = TryCheckin();
                if (ok) f.Close();
            };
            f.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(_imageForm, f)) _imageForm = null;
            };

            _imageForm = f;
            f.ShowAtBottomRight();
            AppLog.Info("打开打卡示意图");
        }
        catch (Exception ex)
        {
            AppLog.Warn("打开示意图失败: " + ex);
        }
    }

    // ---------- 业务操作 ----------

    public (bool ok, string msg) TryCheckin()
    {
        var now = DateTimeOffset.Now;
        var err = Logic.ValidateCheckin(Store.Settings, Store.Records, now);
        if (err is not null)
        {
            try
            {
                _tray.ShowBalloonTip(5000, "现在还不能打卡", err, ToolTipIcon.Warning);
            }
            catch { }
            return (false, err);
        }

        var rec = Logic.AddRecord(Store.Settings, Store.Records, now);
        Store.SaveRecords();

        // 打卡完成 = 本轮结束：清掉轮次标记，下一轮仍按“上次提醒 + 倒计时”走，
        // 但会被“上次打卡 + 最小间隔”顶住，不会提前弹
        Store.Runtime.SnoozeUntil = null;
        Store.Runtime.RoundFiredAt = null;
        Store.Runtime.RoundStage = -1;
        Store.Runtime.WaitingForActivity = false;
        Store.SaveRuntime();
        AppLog.Info($"打卡记录 {rec.Ts:HH:mm} ({rec.Date})");

        RefreshUi();
        return (true, "");
    }

    public void ResetReminderRound()
    {
        Store.Runtime.SnoozeUntil = null;
        Store.SaveRuntime();
    }

    public void RefreshUi()
    {
        try
        {
            var s = Store.Settings;
            var now = DateTimeOffset.Now;
            var st = Logic.Compute(s, Store.Records, now);

            var state = st.IsFinished ? "done" : st.CanCheckNow ? "action" : "wait";
            if (state != _iconState)
            {
                _iconState = state;
                _tray.Icon = IconFactory.Create(state);
            }

            var rt = Store.Runtime;
            var due = Schedule.CurrentDue(s, st, rt);

            var tip = st.IsFinished
                ? $"今天 {st.Done}/{s.DailyLimit} 已打满"
                : rt.WaitingForActivity
                    ? $"到点了，动一下鼠标就提醒（{st.Done}/{s.DailyLimit}）"
                    : st.CanCheckNow
                        ? $"现在可以打卡（{st.Done}/{s.DailyLimit}）"
                        : $"下次 {Logic.Fmt(st.NextEarliest)} 可打卡（{st.Done}/{s.DailyLimit}）";
            _tray.Text = tip.Length > 63 ? tip[..63] : tip;

            _miStatus.Text = st.IsFinished
                ? $"今天 {st.Done}/{s.DailyLimit} · 已完成"
                : rt.WaitingForActivity
                    ? $"今天 {st.Done}/{s.DailyLimit} · 到点了，等你操作"
                    : $"今天 {st.Done}/{s.DailyLimit} · 下次提醒 {Logic.Fmt(due)}";

            _miMute.Checked = Store.Runtime.MutedToday;
            _miAutoRun.Checked = s.AutoStart;

            if (_main is { IsDisposed: false, Visible: true }) _main.RefreshState();
        }
        catch (Exception ex)
        {
            AppLog.Warn("RefreshUi 异常: " + ex.Message);
        }
    }

    // ---------- 窗口 ----------

    public void ShowMain()
    {
        if (_main is null || _main.IsDisposed)
        {
            _main = new MainForm(this);
        }
        _main.Show();
        if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
        _main.Activate();
        _main.RefreshState();
    }

    public void ShowSettings(IWin32Window? owner)
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsForm(this);
        _settings.Show(owner);
        _settings.FormClosed += (_, _) => { _settings = null; RefreshUi(); };
    }

    public void NotifyHiddenToTray()
    {
        try
        {
            _tray.ShowBalloonTip(3000, "还在后台运行", "程序已收进托盘，提醒照常；右键托盘图标可退出。", ToolTipIcon.Info);
        }
        catch { }
    }

    public void ExportToPhone(bool interactive)
    {
        try
        {
            var path = IcsExporter.WriteFile(Store.Settings);
            AppLog.Info("导出日历: " + path);
            if (!interactive) return;

            try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }

            var times = IcsExporter.ValidTimes(Store.Settings)
                .Select(t => $"{t.Hours:00}:{t.Minutes:00}")
                .ToList();
            var timeText = times.Count == 0 ? "（没有可用的兜底时刻）" : string.Join("、", times);

            MessageBox.Show(
                "已生成手机日历文件：\n" + path + "\n\n" +
                "导入方法（任选一种）：\n" +
                "1. 用微信「文件传输助手」把这个 .ics 文件发到手机，点开后选“添加到日历”；\n" +
                "2. 直接把文件拷到手机，用日历 App 打开导入。\n\n" +
                $"手机端只按固定时刻响：{timeText}（一天 {times.Count} 次）。\n" +
                "它不跟电脑的“首次操作 + 间隔”节拍联动，纯粹是你不坐在电脑前时的兜底。\n" +
                "改了兜底时刻后，重新导出一份再导入即可（旧的日程建议先删掉）。",
                "同步到手机日历", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出失败：" + ex.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- 单实例唤起 ----------

    private void StartShowListener()
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "GuangPanCheckin.ShowWindow");
            var t = new Thread(() =>
            {
                while (!_disposed)
                {
                    try
                    {
                        if (_showEvent is null) return;
                        _showEvent.WaitOne();
                        if (_disposed) return;

                        // 必须走一个「句柄已创建」的控件，否则 BeginInvoke 会抛异常把线程弄死
                        _anchor.BeginInvoke(new Action(() =>
                        {
                            ShowMain();
                            if (_main is { IsDisposed: false }) _main.Activate();
                        }));
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn("唤起窗口失败: " + ex.Message);
                        Thread.Sleep(300);
                    }
                }
            }) { IsBackground = true };
            t.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn("单实例监听失败: " + ex.Message);
        }
    }

    public static void ApplyAutoStart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) return;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            if (on) key.SetValue("GuangPanCheckin", $"\"{exe}\" --autostart");
            else key.DeleteValue("GuangPanCheckin", false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("设置开机自启失败: " + ex.Message);
        }
    }

    private void ExitApp()
    {
        try { _engine.Stop(); } catch { }
        try { _tray.Visible = false; _tray.Dispose(); } catch { }
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        if (disposing)
        {
            try
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch { }
            try { _imageForm?.Dispose(); } catch { }
            try { _popup?.Dispose(); } catch { }
            try { _anchor.Dispose(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            _engine.Dispose();
            _menu.Dispose();
            _showEvent?.Dispose();
        }
        base.Dispose(disposing);
    }
}
