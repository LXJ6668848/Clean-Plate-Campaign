using System.Threading;

namespace GuangPanCheckin;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--shot")
        {
            ApplicationConfiguration.Initialize();
            Shot.Run();
            return;
        }

        if (args.Length > 0 && args[0] == "--selftest")
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }

        if (args.Length > 0 && args[0] == "--uitest")
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode = UiTest.Run();
            return;
        }

        // 诊断用：打印当前空闲秒数，确认“有操作”的判定真的读到了系统输入
        if (args.Length > 0 && args[0] == "--idle")
        {
            var idle = Activity.IdleSeconds();
            Console.WriteLine($"idleSeconds={idle}");
            return;
        }

        // 诊断用：不开界面直接重新导出手机日历
        if (args.Length > 0 && args[0] == "--ics")
        {
            Store.Load();
            var p = IcsExporter.WriteFile(Store.Settings);
            var n = IcsExporter.ValidTimes(Store.Settings).Count;
            Console.WriteLine($"ics={p} timesPerDay={n}");
            return;
        }

        using var mutex = new Mutex(true, @"Local\GuangPanCheckin.SingleInstance", out var isNew);
        if (!isNew)
        {
            // 已经有实例在跑：请它把主窗口显示出来
            try
            {
                using var ev = EventWaitHandle.OpenExisting("GuangPanCheckin.ShowWindow");
                ev.Set();
            }
            catch
            {
                MessageBox.Show(
                    "程序已经在后台运行了。\n\n" +
                    "请到任务栏右下角的托盘区找一个圆形图标（可能被折叠进「^」里），双击它就能打开面板。",
                    "光盘打卡提醒", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        var ctx = new TrayContext();

        // 只有开机自启（--autostart）才安静地待在托盘；手动双击一律把窗口打开
        var silent = args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        if (!silent) ctx.ShowMain();

        Application.Run(ctx);
    }
}
