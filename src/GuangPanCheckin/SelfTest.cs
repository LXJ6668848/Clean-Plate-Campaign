using System.Drawing.Imaging;

namespace GuangPanCheckin;

/// <summary>无界面自检：把边界逻辑跑一遍，结果写到 %APPDATA%\GuangPanCheckin\selftest.log。</summary>
internal static class SelfTest
{
    private static readonly List<string> Lines = new();
    private static int _fail;

    private static DateTimeOffset T(int y, int mo, int d, int h, int mi)
        => new(y, mo, d, h, mi, 0, TimeSpan.FromHours(8));

    private static CheckRecord Rec(DateTimeOffset ts) => new()
    {
        Ts = ts,
        Date = ts.ToString("yyyy-MM-dd"),
        Source = "manual"
    };

    private static AppSettings S() => new()
    {
        DailyLimit = 3,
        MinIntervalMinutes = 120,
        Anchors = new List<string> { "07:20", "11:50", "17:40" },
        QuietStart = "23:00",
        QuietEnd = "07:00",
        IntervalAcrossDays = true
    };

    /// <summary>造一个运行态：默认计时起点 + 2h 到期，未弹过。</summary>
    private static RuntimeState Rt(DateTimeOffset dayStart, int roundStage = -1, DateTimeOffset? firedAt = null)
        => new()
        {
            Date = dayStart.ToString("yyyy-MM-dd"),
            DayStart = dayStart,
            NextDue = dayStart.AddMinutes(120),
            RoundStage = roundStage,
            RoundFiredAt = firedAt
        };

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _fail++;
        Lines.Add($"{DateTime.Now:HH:mm:ss} {(ok ? "PASS" : "FAIL")}  {name}{(detail == "" ? "" : "  -> " + detail)}");
    }

    public static int Run()
    {
        var s = S();

        // 1 空白日
        var st = Logic.Compute(s, new List<CheckRecord>(), T(2026, 10, 18, 6, 0));
        Check("空白日可打卡", st.CanCheckNow && st.Done == 0 && st.Remaining == 3);
        Check("空白日下次最早=0点", st.NextEarliest == T(2026, 10, 18, 0, 0), $"{st.NextEarliest:HH:mm}");
        Check("空白日最晚必打=20:00", st.LatestNeeded == T(2026, 10, 18, 20, 0), $"{Logic.Fmt(st.LatestNeeded)}");

        // 2 一次打卡后的间隔
        var recs = new List<CheckRecord> { Rec(T(2026, 10, 18, 12, 5)) };
        st = Logic.Compute(s, recs, T(2026, 10, 18, 12, 30));
        Check("间隔未满不可打", !st.CanCheckNow);
        Check("下次最早=14:05", st.NextEarliest == T(2026, 10, 18, 14, 5), $"{st.NextEarliest:HH:mm}");
        Check("剩余2次", st.Remaining == 2);

        st = Logic.Compute(s, recs, T(2026, 10, 18, 14, 5));
        Check("到点即可打", st.CanCheckNow);

        var err = Logic.ValidateCheckin(s, recs, T(2026, 10, 18, 13, 0));
        Check("校验拦截提前打卡", err is not null, err ?? "");

        // 3 满 3 次
        recs = new List<CheckRecord>
        {
            Rec(T(2026, 10, 18, 7, 30)), Rec(T(2026, 10, 18, 12, 5)), Rec(T(2026, 10, 18, 18, 10))
        };
        st = Logic.Compute(s, recs, T(2026, 10, 18, 19, 0));
        Check("满3次后完成", st.IsFinished && st.Remaining == 0);
        Check("满3次后不再提醒",
            Schedule.Plan(s, st, Rt(T(2026, 10, 18, 8, 0)), T(2026, 10, 18, 19, 0), 5, false).Action == RemindAction.Idle);
        Check("满3次后校验拒绝", Logic.ValidateCheckin(s, recs, T(2026, 10, 18, 20, 0)) is not null);

        // 4 跨天：23:50 打过，次日 00:10
        var cross = new List<CheckRecord> { Rec(T(2026, 10, 17, 23, 50)) };
        st = Logic.Compute(s, cross, T(2026, 10, 18, 0, 10));
        Check("跨天计数已重置", st.Done == 0);
        Check("跨天仍受间隔约束(不可打)", !st.CanCheckNow, $"next={Logic.Fmt(st.NextEarliest)}");
        Check("跨天最早=01:50", st.NextEarliest == T(2026, 10, 18, 1, 50), $"{st.NextEarliest:HH:mm}");

        var s2 = S();
        s2.IntervalAcrossDays = false;
        st = Logic.Compute(s2, cross, T(2026, 10, 18, 0, 10));
        Check("关闭跨天约束后可打", st.CanCheckNow);

        // 5 时间不够完成全部
        var late = new List<CheckRecord> { Rec(T(2026, 10, 18, 21, 30)) };
        st = Logic.Compute(s, late, T(2026, 10, 18, 21, 40));
        Check("23:30 才可打时最多 1 次", st.MaxAchievable == 1, $"max={st.MaxAchievable} next={Logic.Fmt(st.NextEarliest)}");

        // 6 免打扰
        Check("23:30 属免打扰", Logic.IsQuiet(s, T(2026, 10, 18, 23, 30)));
        Check("06:00 属免打扰", Logic.IsQuiet(s, T(2026, 10, 18, 6, 0)));
        Check("12:00 不属免打扰", !Logic.IsQuiet(s, T(2026, 10, 18, 12, 0)));

        // 7 提醒不再依赖锚点：首轮 = 计时起点 + 间隔，并被“上次打卡 + 间隔”顶住
        var s3 = S();
        s3.Anchors = new List<string> { "07:20" };
        st = Logic.Compute(s3, new List<CheckRecord> { Rec(T(2026, 10, 18, 9, 0)) }, T(2026, 10, 18, 9, 10));
        var plan7 = Schedule.Plan(s3, st, Rt(T(2026, 10, 18, 8, 0)), T(2026, 10, 18, 9, 10), 5, false);
        Check("首轮到期被打卡间隔顶到 11:00", plan7.Due == T(2026, 10, 18, 11, 0) && plan7.Action == RemindAction.Idle,
            $"{Logic.Fmt(plan7.Due)}");

        // 8 日历导出：兜底时刻与次数解耦
        var ics = IcsExporter.Build(s, 3);
        Check("ICS 结构完整", ics.Contains("BEGIN:VCALENDAR") && ics.Contains("END:VCALENDAR")
                              && ics.Contains("BEGIN:VALARM"));
        Check("ICS 事件数=3天*3处时刻", System.Text.RegularExpressions.Regex.Matches(ics, "BEGIN:VEVENT").Count == 9,
            System.Text.RegularExpressions.Regex.Matches(ics, "BEGIN:VEVENT").Count.ToString());

        // 每日上限改成 1，兜底时刻不该被截断（旧版会按次数截断成每天 1 条）
        var sOne = S();
        sOne.DailyLimit = 1;
        var icsOne = IcsExporter.Build(sOne, 3);
        Check("上限=1 时兜底时刻不被截断",
            System.Text.RegularExpressions.Regex.Matches(icsOne, "BEGIN:VEVENT").Count == 9,
            System.Text.RegularExpressions.Regex.Matches(icsOne, "BEGIN:VEVENT").Count.ToString());

        // 上限=0 或不填时刻：一条都不生成，但要能正常出结构
        var sZero = S();
        sZero.Anchors = new List<string>();
        var icsZero = IcsExporter.Build(sZero, 3);
        Check("时刻全空时不生成事件却仍是合法日历",
            System.Text.RegularExpressions.Regex.Matches(icsZero, "BEGIN:VEVENT").Count == 0
            && icsZero.Contains("END:VCALENDAR"));

        // 空串、乱填、重复都该被吃掉；顺序统一成升序
        var sDirty = S();
        sDirty.Anchors = new List<string> { "18:00", "", "abc", "08:00", "18:00", "12:00" };
        var times = IcsExporter.ValidTimes(sDirty);
        Check("兜底时刻去重/过滤/排序", times.Count == 3
            && times[0] == TimeSpan.FromHours(8) && times[1] == TimeSpan.FromHours(12) && times[2] == TimeSpan.FromHours(18),
            string.Join(",", times));

        // 标题里不该再出现「第 N 次」，改成时刻本身
        var icsTitles = IcsExporter.Build(sDirty, 1);
        Check("事件标题用时刻而非次数",
            !icsTitles.Contains("第 1 次") && icsTitles.Contains("SUMMARY:光盘打卡提醒（08:00）")
            && icsTitles.Contains("SUMMARY:光盘打卡提醒（12:00）") && icsTitles.Contains("SUMMARY:光盘打卡提醒（18:00）"));

        // 9 界面可构造（不显示）
        try
        {
            using var f1 = new MainForm(null);
            f1.CreateControl();
            using var f2 = new SettingsForm(null);
            f2.CreateControl();
            using var f3 = new ReminderForm();
            f3.CreateControl();
            Check("三个窗口可正常构造", true);
        }
        catch (Exception ex)
        {
            Check("三个窗口可正常构造", false, ex.Message);
        }

        // 10 打卡示意图资产（在临时目录里跑，不碰真实数据）
        var tmp = Path.Combine(Path.GetTempPath(), "gpc-selftest");
        try
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);

            var good = Path.Combine(tmp, "good.png");
            using (var bmp = new Bitmap(60, 90))
            {
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.Orange);
                bmp.Save(good, ImageFormat.Png);
            }

            var (imgGood, errGood) = AssetStore.LoadFrom(good);
            Check("正常图片可加载", imgGood is not null, errGood);
            if (imgGood is not null)
                Check("图片尺寸读对", imgGood.Width == 60 && imgGood.Height == 90, $"{imgGood.Width}x{imgGood.Height}");
            imgGood?.Dispose();

            var (imgMissing, _) = AssetStore.LoadFrom(Path.Combine(tmp, "nope.jpg"));
            Check("缺失图片返回空", imgMissing is null);

            var bad = Path.Combine(tmp, "bad.jpg");
            File.WriteAllText(bad, "这不是图片");
            var (imgBad, _) = AssetStore.LoadFrom(bad);
            Check("损坏图片返回空", imgBad is null);

            var txt = Path.Combine(tmp, "a.txt");
            File.WriteAllText(txt, "x");
            Check("不支持格式被拒绝", !AssetStore.ImportTo(txt, Path.Combine(tmp, "assets")).ok);

            var dest = Path.Combine(tmp, "assets");
            var (okImport, imported) = AssetStore.ImportTo(good, dest);
            Check("导入成功并落盘", okImport && File.Exists(imported), imported);
            Check("导入文件名固定为 target.*", Path.GetFileName(imported).StartsWith("target."), imported);

            var (imgAgain, _) = AssetStore.LoadFrom(imported);
            Check("导入后可再次读取", imgAgain is not null);
            imgAgain?.Dispose();

            using var fimg = new ImageForm(new Bitmap(60, 90));
            fimg.CreateControl();
            Check("图片窗可构造", fimg.Width > 0 && fimg.Height > 90, $"{fimg.Width}x{fimg.Height}");
        }
        catch (Exception ex)
        {
            Check("示意图用例", false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        // 11 提醒节拍：当天首次操作起点 + 两小时倒计时 + 活跃门控
        var s4 = S();
        var empty = new List<CheckRecord>();
        var dayStart = T(2026, 10, 19, 8, 0);

        var rtNone = new RuntimeState { Date = "2026-10-19" };
        Check("还没检测到操作时不提醒",
            Schedule.Plan(s4, Logic.Compute(s4, empty, dayStart), rtNone, dayStart, 5, false).Action == RemindAction.Idle);

        var rtA = Rt(dayStart);
        Check("起点后 1h59m 不提醒",
            Schedule.Plan(s4, Logic.Compute(s4, empty, T(2026, 10, 19, 9, 59)), rtA, T(2026, 10, 19, 9, 59), 5, false).Action == RemindAction.Idle);

        var st10 = Logic.Compute(s4, empty, T(2026, 10, 19, 10, 0));
        var pIdle = Schedule.Plan(s4, st10, rtA, T(2026, 10, 19, 10, 0), 900, false);
        Check("到点但无人操作 -> 压着不弹", pIdle.Action == RemindAction.Deferred, pIdle.Reason);

        var pActive = Schedule.Plan(s4, st10, rtA, T(2026, 10, 19, 10, 0), 8, false);
        Check("到点且有操作 -> 弹", pActive.Action == RemindAction.Fire && pActive.Stage == 0, pActive.Reason);

        var pBack = Schedule.Plan(s4, st10, rtA, T(2026, 10, 19, 10, 40), 5, false);
        Check("人回来立刻补弹（哪怕早过点了）", pBack.Action == RemindAction.Fire, pBack.Reason);

        Check("锁屏期间一律不弹",
            Schedule.Plan(s4, st10, rtA, T(2026, 10, 19, 10, 0), 2, true).Action == RemindAction.Deferred);

        var rtFired = Rt(dayStart, 0, T(2026, 10, 19, 10, 0));
        rtFired.NextDue = T(2026, 10, 19, 12, 0);
        Check("下一轮 = 弹出时刻 + 2h",
            Schedule.Plan(s4, Logic.Compute(s4, empty, T(2026, 10, 19, 12, 0)), rtFired, T(2026, 10, 19, 12, 0), 5, false).Action == RemindAction.Fire);

        var st1011 = Logic.Compute(s4, empty, T(2026, 10, 19, 10, 11));
        var pUp = Schedule.Plan(s4, st1011, rtFired, T(2026, 10, 19, 10, 11), 5, false);
        Check("未处理则 10 分钟后升级一次", pUp.Action == RemindAction.Fire && pUp.Stage == 1, $"{pUp.Stage}");

        var pUpIdle = Schedule.Plan(s4, st1011, rtFired, T(2026, 10, 19, 10, 11), 600, false);
        Check("升级提醒同样要求有操作", pUpIdle.Action == RemindAction.Deferred, pUpIdle.Reason);

        var rtMax = Rt(dayStart, 2, T(2026, 10, 19, 10, 0));
        rtMax.NextDue = T(2026, 10, 19, 12, 0);
        Check("最后一次之后不再加催",
            Schedule.Plan(s4, st1011, rtMax, T(2026, 10, 19, 10, 50), 5, false).Action == RemindAction.Idle);

        var quiet = Schedule.Plan(s4, Logic.Compute(s4, empty, T(2026, 10, 19, 23, 30)), Rt(T(2026, 10, 19, 20, 0)),
            T(2026, 10, 19, 23, 30), 5, false);
        Check("免打扰时段不弹", quiet.Action == RemindAction.Idle);

        var rtDay = Rt(dayStart, 2, T(2026, 10, 19, 10, 0));
        rtDay.WaitingForActivity = true;
        var changed = Schedule.ResetIfNewDay(rtDay, T(2026, 10, 20, 0, 0));
        Check("跨天清空计时起点与轮次", changed && rtDay.DayStart is null && rtDay.NextDue is null
            && rtDay.RoundStage == -1 && !rtDay.WaitingForActivity);

        var afterMidnight = T(2026, 10, 20, 0, 5);
        Check("新一天未操作前不提醒",
            Schedule.Plan(s4, Logic.Compute(s4, empty, afterMidnight), rtDay, afterMidnight, 5, false).Action == RemindAction.Idle);

        // 休眠跨越到期：NextDue 是绝对时间，醒来即视为“到期”，但还要有操作才弹
        var rtSleep = Rt(dayStart);
        var wake = T(2026, 10, 19, 15, 0);
        Check("睡醒后到点即补弹",
            Schedule.Plan(s4, Logic.Compute(s4, empty, wake), rtSleep, wake, 5, false).Action == RemindAction.Fire);
        Check("睡醒但没动键鼠则不弹",
            Schedule.Plan(s4, Logic.Compute(s4, empty, wake), rtSleep, wake, 1200, false).Action == RemindAction.Deferred);

        // 12 活跃判定本身
        Check("空闲 30s 算有操作", Schedule.IsActive(s4, 30));
        Check("空闲 61s 不算有操作", !Schedule.IsActive(s4, 61));

        Lines.Add("");
        Lines.Add(_fail == 0 ? "全部通过" : $"失败 {_fail} 项");
        try
        {
            Store.EnsureDir();
            File.WriteAllText(Path.Combine(Store.Dir, "selftest.log"), string.Join("\r\n", Lines));
        }
        catch { }
        return _fail;
    }
}
