using System.Text;

namespace GuangPanCheckin;

public static class IcsExporter
{
    /// <summary>一天最多导出几个兜底时刻，与设置页的行数保持一致。</summary>
    public const int MaxPerDay = 5;

    /// <summary>
    /// 生成手机日历文件：每天在你设定的「兜底时刻」各响一次。
    /// 注意：这里与打卡次数无关——电脑端按「当天首次操作 + 间隔」滚动提醒，
    /// 手机端做不了这种推算，只能按固定时刻响，纯粹是「人不在电脑前」时的保险。
    /// </summary>
    public static string Build(AppSettings s, int days = 30)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//GuangPanCheckin//CN");
        sb.AppendLine("CALSCALE:GREGORIAN");
        sb.AppendLine("METHOD:PUBLISH");
        sb.AppendLine("X-WR-CALNAME:光盘打卡");
        sb.AppendLine("X-WR-TIMEZONE:Asia/Shanghai");

        var start = DateTime.Today;
        var times = ValidTimes(s);

        for (var d = 0; d < days; d++)
        {
            var day = start.AddDays(d);
            for (var k = 0; k < times.Count; k++)
            {
                var dt = day.Add(times[k]);
                var hhmm = $"{times[k].Hours:00}:{times[k].Minutes:00}";

                sb.AppendLine("BEGIN:VEVENT");
                sb.AppendLine($"UID:gp-{day:yyyyMMdd}-{times[k].Hours:00}{times[k].Minutes:00}@guangpan.local");
                sb.AppendLine($"DTSTAMP:{DateTime.UtcNow:yyyyMMdd\\THHmmss\\Z}");
                sb.AppendLine($"DTSTART:{dt:yyyyMMdd\\THHmmss}");
                sb.AppendLine($"DTEND:{dt.AddMinutes(15):yyyyMMdd\\THHmmss}");
                sb.AppendLine($"SUMMARY:光盘打卡提醒（{hhmm}）");
                sb.AppendLine($"DESCRIPTION:手机端固定时刻兜底提醒。电脑端不按固定时间弹，改为“当天第一次操作电脑起每 {s.MinIntervalMinutes} 分钟一轮，且只有你在操作时才弹”。每日最多 {s.DailyLimit} 次，两次间隔至少 {s.MinIntervalMinutes} 分钟。");
                sb.AppendLine("LOCATION:食堂");
                sb.AppendLine("BEGIN:VALARM");
                sb.AppendLine("TRIGGER:PT0M");
                sb.AppendLine("ACTION:DISPLAY");
                sb.AppendLine("DESCRIPTION:该打卡了");
                sb.AppendLine("END:VALARM");
                sb.AppendLine("END:VEVENT");
            }
        }

        sb.AppendLine("END:VCALENDAR");
        return sb.ToString();
    }

    /// <summary>取配置里所有合法的 HH:mm，去重升序，最多 MaxPerDay 个。</summary>
    public static List<TimeSpan> ValidTimes(AppSettings s)
    {
        return (s.Anchors ?? new List<string>())
            .Select(Logic.ParseTime)
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .Distinct()
            .OrderBy(t => t)
            .Take(MaxPerDay)
            .ToList();
    }

    public static string WriteFile(AppSettings s)
    {
        Store.EnsureDir();
        var path = Path.Combine(Store.Dir, "guangpan.ics");
        File.WriteAllText(path, Build(s), new UTF8Encoding(false));
        return path;
    }
}
