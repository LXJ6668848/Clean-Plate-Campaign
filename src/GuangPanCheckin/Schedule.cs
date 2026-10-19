namespace GuangPanCheckin;

public enum RemindAction
{
    /// <summary>还没到点，或者今天不需要再提醒。</summary>
    Idle,
    /// <summary>到点且检测到人在操作 → 弹。</summary>
    Fire,
    /// <summary>到点了，但没检测到操作 → 先压着，等下一次输入再弹。</summary>
    Deferred
}

public sealed class RemindPlan
{
    public RemindAction Action { get; init; } = RemindAction.Idle;
    public int Stage { get; init; } = -1;
    public DateTimeOffset? Due { get; init; }
    public string Reason { get; init; } = "";

    public static RemindPlan None => new();
}

/// <summary>
/// 提醒节拍：以「当天第一次操作电脑」为起点，每 2 小时一轮；
/// 到点时必须检测到真实的键鼠输入才弹，否则一直压着等下一次操作。
/// 全部是纯函数，方便自检把跨天/休眠/锁屏这些场景跑一遍。
/// </summary>
public static class Schedule
{
    /// <summary>距最后一次键鼠输入不超过阈值，就算「正在操作」。</summary>
    public static bool IsActive(AppSettings s, long idleSeconds) => idleSeconds <= Math.Max(1, s.ActiveIdleSeconds);

    public static DateTimeOffset FirstDue(DateTimeOffset dayStart, AppSettings s)
        => dayStart.AddMinutes(Math.Max(1, s.MinIntervalMinutes));

    /// <summary>本轮实际会弹的最早时刻：到期时间，但不能早于「上次打卡 + 最小间隔」。</summary>
    public static DateTimeOffset? CurrentDue(AppSettings s, DayState st, RuntimeState rt)
    {
        if (rt.DayStart is not { } dayStart) return null;

        var due = rt.NextDue ?? FirstDue(dayStart, s);
        if (st.NextEarliest is { } earliest && earliest > due) due = earliest;
        return due;
    }

    /// <summary>决定这一刻要不要弹。</summary>
    public static RemindPlan Plan(AppSettings s, DayState st, RuntimeState rt,
        DateTimeOffset now, long idleSeconds, bool locked)
    {
        // 今天已经打满 / 手动静音 / 刚选了“10 分钟后再提醒” / 免打扰时段
        if (st.Remaining <= 0) return RemindPlan.None;
        if (rt.MutedToday) return RemindPlan.None;
        if (rt.SnoozeUntil is { } snooze && now < snooze) return RemindPlan.None;
        if (Logic.IsQuiet(s, now)) return RemindPlan.None;

        // 今天还没动过电脑：计时起点不存在，就不可能有“到期”
        if (rt.DayStart is not { } dayStart) return RemindPlan.None;

        // 锁屏期间一律当作没人：哪怕系统报来奇怪的输入事件也不弹
        var active = !locked && IsActive(s, idleSeconds);

        var due = rt.NextDue ?? FirstDue(dayStart, s);
        if (st.NextEarliest is { } earliest && earliest > due) due = earliest;

        // 1) 到点：有操作就弹，没操作就压着
        if (now >= due)
        {
            if (!active)
            {
                return new RemindPlan
                {
                    Action = RemindAction.Deferred,
                    Due = due,
                    Reason = locked ? "到期但屏幕已锁定" : $"到期但空闲 {idleSeconds}s（阈值 {s.ActiveIdleSeconds}s）"
                };
            }
            return new RemindPlan
            {
                Action = RemindAction.Fire,
                Stage = 0,
                Due = due,
                Reason = $"到期且有操作（空闲 {idleSeconds}s）"
            };
        }

        // 2) 本轮已弹过但没打卡：+10 / +30 分钟加催，同样要求有操作
        if (rt.RoundFiredAt is { } firedAt && rt.RoundStage is >= 0 and < 2)
        {
            var t1 = firedAt.AddMinutes(Math.Max(1, s.Escalate1Minutes));
            var t2 = firedAt.AddMinutes(Math.Max(s.Escalate1Minutes, s.Escalate2Minutes));

            var stage = rt.RoundStage == 0
                ? (now >= t2 ? 2 : now >= t1 ? 1 : -1)
                : (now >= t2 ? 2 : -1);

            if (stage > rt.RoundStage)
            {
                if (!active)
                {
                    return new RemindPlan
                    {
                        Action = RemindAction.Deferred,
                        Due = due,
                        Reason = $"升级提醒到点但空闲 {idleSeconds}s"
                    };
                }
                return new RemindPlan
                {
                    Action = RemindAction.Fire,
                    Stage = stage,
                    Due = due,
                    Reason = $"升级提醒（空闲 {idleSeconds}s）"
                };
            }
        }

        return new RemindPlan { Action = RemindAction.Idle, Due = due };
    }

    /// <summary>
    /// 跨天重置：清空计时起点与轮次，等新的一天第一次操作再重新初始化。
    /// 返回 true 表示确实发生了重置（调用方负责落盘）。
    /// </summary>
    public static bool ResetIfNewDay(RuntimeState rt, DateTimeOffset now)
    {
        var today = now.LocalDateTime.ToString("yyyy-MM-dd");
        if (rt.Date == today) return false;

        rt.Date = today;
        rt.DayStart = null;
        rt.NextDue = null;
        rt.RoundFiredAt = null;
        rt.RoundStage = -1;
        rt.WaitingForActivity = false;
        rt.MutedToday = false;
        rt.SnoozeUntil = null;
        return true;
    }
}
