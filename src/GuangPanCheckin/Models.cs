using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuangPanCheckin;

/// <summary>一条打卡记录。派生值（次数/下次时间）一律不落库，全部实时计算。</summary>
public sealed class CheckRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Ts { get; set; }
    /// <summary>归属日 yyyy-MM-dd，按 00:00 日切计算。</summary>
    public string Date { get; set; } = "";
    public string? Note { get; set; }
    /// <summary>manual | backfill | corrected</summary>
    public string Source { get; set; } = "manual";
}

/// <summary>用户可配置项。</summary>
public sealed class AppSettings
{
    public int DailyLimit { get; set; } = 3;
    public int MinIntervalMinutes { get; set; } = 120;
    /// <summary>
    /// 仅用于导出手机日历的兜底提醒时刻（HH:mm），与打卡次数无关，也不参与电脑弹窗。
    /// 电脑端按「当天首次操作 + 间隔」走滚动节拍，手机端只能按固定时刻响，两者互不绑定。
    /// </summary>
    public List<string> Anchors { get; set; } = new() { "07:20", "11:50", "17:40" };
    public string QuietStart { get; set; } = "23:00";
    public string QuietEnd { get; set; } = "07:00";
    public bool SoundEnabled { get; set; } = true;
    public bool PopupEnabled { get; set; } = true;
    public bool BalloonEnabled { get; set; } = true;
    /// <summary>跨天是否也受最小间隔约束（23:50 打过，次日 00:10 能否打）。</summary>
    public bool IntervalAcrossDays { get; set; } = true;
    public int Escalate1Minutes { get; set; } = 10;
    public int Escalate2Minutes { get; set; } = 30;
    public bool AutoStart { get; set; } = true;
    /// <summary>「去打卡」时展示的示意图路径（默认已复制到 AppData\assets 下）。</summary>
    public string TargetImagePath { get; set; } = "";
    /// <summary>距最后一次键鼠输入不超过这么多秒，才算「正在操作电脑」，到期才会真正弹窗。</summary>
    public int ActiveIdleSeconds { get; set; } = 60;
}

/// <summary>当天运行态（计时起点、本轮提醒是否已弹），丢了最多重弹一次，不影响打卡记录。</summary>
public sealed class RuntimeState
{
    public string Date { get; set; } = "";
    public bool MutedToday { get; set; }
    public DateTimeOffset? SnoozeUntil { get; set; }

    /// <summary>今天的计时起点：当天第一次检测到键鼠操作的时刻。每天只初始化一次。</summary>
    public DateTimeOffset? DayStart { get; set; }

    /// <summary>当前这一轮提醒的到期时间（= 上一轮弹出时刻 + 倒计时；首轮 = 计时起点 + 倒计时）。</summary>
    public DateTimeOffset? NextDue { get; set; }

    /// <summary>本轮第一次弹出的时刻；null 表示本轮还没弹过。</summary>
    public DateTimeOffset? RoundFiredAt { get; set; }

    /// <summary>本轮已弹到第几级：-1 未弹，0 首提醒，1 第二次，2 最后一次。</summary>
    public int RoundStage { get; set; } = -1;

    /// <summary>已经到点，但没检测到人在操作，先压着不弹（只用于界面提示与日志）。</summary>
    public bool WaitingForActivity { get; set; }
}

public static class Store
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GuangPanCheckin");

    public static readonly string BackupDir = Path.Combine(Dir, "backups");

    private static readonly string RecordsPath = Path.Combine(Dir, "records.json");
    private static readonly string SettingsPath = Path.Combine(Dir, "settings.json");
    private static readonly string RuntimePath = Path.Combine(Dir, "runtime.json");

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static List<CheckRecord> Records { get; private set; } = new();
    public static AppSettings Settings { get; private set; } = new();
    public static RuntimeState Runtime { get; private set; } = new();

    /// <summary>是否已经 Load 过。未加载时的内存状态是空的，绝不能拿它去覆盖磁盘。</summary>
    public static bool Loaded { get; private set; }

    public static void EnsureDir()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(BackupDir);
    }

    public static void Load()
    {
        EnsureDir();
        Records = Read<List<CheckRecord>>(RecordsPath) ?? new List<CheckRecord>();
        Settings = Read<AppSettings>(SettingsPath) ?? new AppSettings();
        Runtime = Read<RuntimeState>(RuntimePath) ?? new RuntimeState();
        Loaded = true;
        NormalizeRuntime();
        DailyBackup();
    }

    private static T? Read<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Opt);
        }
        catch
        {
            // 文件损坏时保留现场，避免直接覆盖掉用户数据
            try { File.Copy(path, path + ".bad", true); } catch { }
            return default;
        }
    }

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opt));
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写入 {Path.GetFileName(path)} 失败: {ex.Message}");
        }
    }

    public static void SaveRecords()
    {
        if (!Loaded) return;
        Write(RecordsPath, Records);
    }

    public static void SaveSettings()
    {
        if (!Loaded) return; // 未加载就保存，等于把用户配置清空
        Write(SettingsPath, Settings);
    }

    public static void SaveRuntime()
    {
        if (!Loaded) return;
        Write(RuntimePath, Runtime);
    }

    /// <summary>日期变了就重置运行态：计时起点与轮次全部清空，等新的一天第一次操作再初始化。</summary>
    public static void NormalizeRuntime()
    {
        // 还没 Load 过（比如 --selftest 只测界面）时，内存里是空状态，
        // 这时候做“跨天判断”一定会判成新的一天，然后把用户真实的 runtime.json 覆盖掉
        if (!Loaded) return;

        if (!Schedule.ResetIfNewDay(Runtime, DateTimeOffset.Now)) return;
        SaveRuntime();
    }

    /// <summary>每天首次启动备份一次，保留最近 30 份。</summary>
    private static void DailyBackup()
    {
        try
        {
            if (!File.Exists(RecordsPath) || Records.Count == 0) return;
            var target = Path.Combine(BackupDir, $"records-{DateTime.Now:yyyy-MM-dd}.json");
            if (!File.Exists(target)) File.Copy(RecordsPath, target);

            var old = Directory.GetFiles(BackupDir, "records-*.json")
                .OrderByDescending(f => f)
                .Skip(30)
                .ToList();
            foreach (var f in old) { try { File.Delete(f); } catch { } }
        }
        catch { }
    }
}

public static class AppLog
{
    private static readonly string Path = System.IO.Path.Combine(Store.Dir, "app.log");

    public static void Warn(string msg) => Write("WARN " + msg);
    public static void Info(string msg) => Write("INFO " + msg);

    private static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory(Store.Dir);
            File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\r\n");
        }
        catch { }
    }
}
