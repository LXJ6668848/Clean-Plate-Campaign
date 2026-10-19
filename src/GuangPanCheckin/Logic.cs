namespace GuangPanCheckin;

/// <summary>某一天的派生状态。所有字段都是算出来的，不落库。</summary>
public sealed class DayState
{
    public DateOnly Date { get; init; }
    public int Done { get; init; }
    public int Remaining { get; init; }
    public DateTimeOffset? LastCheckin { get; init; }
    public DateTimeOffset? NextEarliest { get; init; }
    public DateTimeOffset? LatestNeeded { get; init; }
    public int MaxAchievable { get; init; }
    public bool CanCheckNow { get; init; }
    public DateTimeOffset EndOfDay { get; init; }

    public bool IsFinished => Remaining == 0;
}

public static class Logic
{
    public static TimeSpan? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Trim().Split(':', '：');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return null;
        if (h is < 0 or > 23 || m is < 0 or > 59) return null;
        return new TimeSpan(h, m, 0);
    }

    public static string Fmt(DateTimeOffset? t) => t is null ? "--:--" : t.Value.ToString("HH:mm");

    /// <summary>核心计算：以 now 为基准推出今天的所有派生值。</summary>
    public static DayState Compute(AppSettings s, IReadOnlyList<CheckRecord> all, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var dayStart = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), now.Offset);
        var endOfDay = dayStart.AddDays(1);

        var todayRecs = all
            .Where(r => r.Date == dayStart.ToString("yyyy-MM-dd"))
            .OrderBy(r => r.Ts)
            .ToList();

        var limit = Math.Max(1, s.DailyLimit);
        var done = Math.Min(todayRecs.Count, limit);
        var remaining = Math.Max(0, limit - done);

        DateTimeOffset? lastToday = todayRecs.Count > 0 ? todayRecs[^1].Ts : null;

        // 跨天是否受间隔约束：先取今天的上次；若今天还没打且要求跨天计算，则回看历史最近一条
        DateTimeOffset? lastAny = lastToday;
        if (lastAny is null && s.IntervalAcrossDays)
            lastAny = all.Where(r => r.Ts <= now).OrderBy(r => r.Ts).LastOrDefault()?.Ts;

        DateTimeOffset nextEarliest = lastAny is null
            ? dayStart
            : lastAny.Value.AddMinutes(Math.Max(1, s.MinIntervalMinutes));
        if (nextEarliest < dayStart) nextEarliest = dayStart;

        DateTimeOffset? latestNeeded = null;
        if (remaining > 0)
            latestNeeded = endOfDay.AddMinutes(-(double)(remaining - 1) * Math.Max(1, s.MinIntervalMinutes));

        var maxAchievable = 0;
        if (remaining > 0)
        {
            var span = (endOfDay - nextEarliest).TotalMinutes;
            var fit = 1 + (int)Math.Floor(Math.Max(0, span) / Math.Max(1, s.MinIntervalMinutes));
            maxAchievable = Math.Min(remaining, fit);
        }

        return new DayState
        {
            Date = today,
            Done = done,
            Remaining = remaining,
            LastCheckin = lastToday,
            NextEarliest = nextEarliest,
            LatestNeeded = latestNeeded,
            MaxAchievable = maxAchievable,
            CanCheckNow = remaining > 0 && now >= nextEarliest,
            EndOfDay = endOfDay
        };
    }

    /// <summary>免打扰时段（支持跨零点）。</summary>
    public static bool IsQuiet(AppSettings s, DateTimeOffset now)
    {
        var start = ParseTime(s.QuietStart);
        var end = ParseTime(s.QuietEnd);
        if (start is null || end is null || start == end) return false;

        var t = now.LocalDateTime.TimeOfDay;
        return start < end
            ? t >= start.Value && t < end.Value
            : t >= start.Value || t < end.Value;
    }

    /// <summary>把“还剩多久”说成人话。</summary>
    public static string Humanize(TimeSpan span)
    {
        if (span.TotalSeconds <= 0) return "随时";
        var h = (int)span.TotalHours;
        var m = span.Minutes;
        if (h > 0 && m > 0) return $"{h} 小时 {m} 分";
        if (h > 0) return $"{h} 小时";
        return $"{Math.Max(1, m)} 分钟";
    }

    /// <summary>截止线显示：次日 0 点一律显示成 24:00，避免出现误导的 00:00。</summary>
    public static string FmtDeadline(DateTimeOffset? t, DateOnly today)
    {
        if (t is null) return "--:--";
        var local = t.Value.LocalDateTime;
        return DateOnly.FromDateTime(local) > today ? "24:00" : t.Value.ToString("HH:mm");
    }

    /// <summary>校验一次打卡是否合法，返回不合法原因（null 表示可以打）。</summary>
    public static string? ValidateCheckin(AppSettings s, IReadOnlyList<CheckRecord> all, DateTimeOffset now)
    {
        var st = Compute(s, all, now);
        if (st.Remaining <= 0) return $"今天已经完成 {st.Done} 次，达到每日上限。";
        if (!st.CanCheckNow)
        {
            var wait = (st.NextEarliest!.Value - now);
            return $"距离上次打卡不足 {s.MinIntervalMinutes} 分钟，最早 {st.NextEarliest:HH:mm} 才能再打（还要等 {Humanize(wait)}）。";
        }
        return null;
    }

    public static CheckRecord AddRecord(AppSettings s, List<CheckRecord> all, DateTimeOffset ts, string source = "manual", string? note = null)
    {
        var rec = new CheckRecord
        {
            Ts = ts,
            Date = new DateTimeOffset(ts.LocalDateTime.Date, ts.Offset).ToString("yyyy-MM-dd"),
            Source = source,
            Note = note
        };
        all.Add(rec);
        return rec;
    }
}
