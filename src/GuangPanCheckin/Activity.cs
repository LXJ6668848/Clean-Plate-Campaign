using System.Runtime.InteropServices;

namespace GuangPanCheckin;

/// <summary>
/// 「用户此刻是否在操作电脑」的判定。
/// 依据 Windows 全局最后输入时间（GetLastInputInfo），统计键鼠事件，不依赖本程序窗口是否有焦点。
/// </summary>
public static class Activity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>
    /// 距离最后一次键鼠输入过去了多少秒。
    /// 取不到时返回 0（按「正在操作」处理：宁可多弹一次，也不要因为读不到而彻底不提醒）。
    /// </summary>
    public static long IdleSeconds()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return 0;

            // dwTime 是 32 位 tick，与 Environment.TickCount 同源；
            // 用无符号差值，避免 49.7 天回绕时算出巨大或负数的空闲时间
            var idleMs = unchecked((uint)(Environment.TickCount - (int)info.dwTime));
            return idleMs / 1000u;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>最后一次键鼠输入的时刻（约等于 now - idle）。</summary>
    public static DateTimeOffset LastInputAt(DateTimeOffset now) => now.AddSeconds(-IdleSeconds());
}
