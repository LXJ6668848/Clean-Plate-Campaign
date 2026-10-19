using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GuangPanCheckin;

/// <summary>开发期用的点击链路验证：真的点一下「去打卡」，看图片窗有没有出来。</summary>
internal static class UiTest
{
    private static readonly List<string> Lines = new();
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _fail++;
        Lines.Add($"{DateTime.Now:HH:mm:ss} {(ok ? "PASS" : "FAIL")}  {name}{(detail == "" ? "" : "  -> " + detail)}");
    }

    private static void Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    public static int Run()
    {
        // 构造 TrayContext 会重写开机自启，先存后恢复，避免污染用户配置
        string? saved = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", false);
            saved = key?.GetValue("GuangPanCheckin") as string;
        }
        catch { }

        try
        {
            Store.Load();
            var ctx = new TrayContext();
            Pump(300);

            // 1 卡片上的「去打卡」按钮存在且能触发事件
            var s = Store.Settings;
            var st = Logic.Compute(s, Store.Records, DateTimeOffset.Now);
            var card = new ReminderForm();
            var goFired = false;
            card.GoCheckin += () => goFired = true;
            card.Present(0, st, s);
            Pump(200);

            var btnGo = card.Controls.OfType<Button>().FirstOrDefault(b => b.Text == "去打卡");
            Check("卡片有「去打卡」按钮", btnGo is not null);
            Check("卡片仍有「我已打卡」按钮", card.Controls.OfType<Button>().Any(b => b.Text == "我已打卡"));
            btnGo?.PerformClick();
            Check("点「去打卡」触发事件", goFired);
            Check("点完卡片自己关闭", card.IsDisposed || !card.Visible);

            // 2 真正走宿主逻辑：打开图片窗
            ctx.ShowImageCard();
            Pump(600);

            var form = Application.OpenForms.OfType<ImageForm>().FirstOrDefault();
            Check("图片窗已弹出", form is { Visible: true } && !form.IsDisposed);

            if (form is not null)
            {
                var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                var rightGap = wa.Right - form.Right;
                var bottomGap = wa.Bottom - form.Bottom;
                Check("图片窗贴在右下角", rightGap is >= 0 and <= 40 && bottomGap is >= 0 and <= 40,
                    $"right={rightGap} bottom={bottomGap}");
                Check("图片窗宽度 320", form.Width == 320, form.Width.ToString());
                Check("图片窗高度含底部按钮条", form.Height > 56, form.Height.ToString());
                Check("图片窗不进任务栏", !form.ShowInTaskbar);
            }

            // 3 重复点不叠加窗口
            ctx.ShowImageCard();
            Pump(400);
            Check("重复打开只有一个图片窗", Application.OpenForms.OfType<ImageForm>().Count() == 1);

            form?.Close();
            Pump(200);
            Check("关闭后图片窗已释放", !Application.OpenForms.OfType<ImageForm>().Any());
        }
        catch (Exception ex)
        {
            Check("点击链路", false, ex.Message);
        }
        finally
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key is not null)
                {
                    if (saved is null) key.DeleteValue("GuangPanCheckin", false);
                    else key.SetValue("GuangPanCheckin", saved);
                }
            }
            catch { }
        }

        Lines.Add("");
        Lines.Add(_fail == 0 ? "全部通过" : $"失败 {_fail} 项");
        try
        {
            Store.EnsureDir();
            File.WriteAllText(Path.Combine(Store.Dir, "uitest.log"), string.Join("\r\n", Lines));
        }
        catch { }
        return _fail;
    }
}
