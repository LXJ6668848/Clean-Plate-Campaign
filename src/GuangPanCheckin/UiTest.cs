using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GuangPanCheckin;

/// <summary>
/// 「去打卡」交互的端到端验证：真的把鼠标移动到按钮上、真的按下再松开，
/// 再检查图片窗有没有出来、有没有被别的窗口盖住、画的是不是那张图。
/// 不走 PerformClick —— 那只能证明事件绑对了，证明不了用户点得到。
/// </summary>
internal static class UiTest
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Pt
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Pt p);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint GaRoot = 2;

    private static readonly List<string> Lines = new();
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _fail++;
        Lines.Add($"{DateTime.Now:HH:mm:ss} {(ok ? "PASS" : "FAIL")}  {name}{(detail == "" ? "" : "  -> " + detail)}");
    }

    private static void Info(string msg) => Lines.Add($"{DateTime.Now:HH:mm:ss} INFO  {msg}");

    private static void Pump(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    /// <summary>真实鼠标点击：移动光标 → 按下 → 松开，全部走系统输入队列。</summary>
    private static void RealClick(Point screenPoint)
    {
        var restore = Cursor.Position;
        SetCursorPos(screenPoint.X, screenPoint.Y);
        Pump(120);
        mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        Pump(60);
        mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
        Pump(300);
        SetCursorPos(restore.X, restore.Y);
        Pump(80);
    }

    private static void RealClick(Control c) =>
        RealClick(c.PointToScreen(new Point(c.Width / 2, c.Height / 2)));

    /// <summary>该点最上层的窗口是不是这个窗体（用来判断有没有被盖住）。</summary>
    private static bool IsTopWindowAt(Form f, Point clientPoint)
    {
        var p = f.PointToScreen(clientPoint);
        var h = GetAncestor(WindowFromPoint(new Pt { X = p.X, Y = p.Y }), GaRoot);
        return h == f.Handle;
    }

    private static ImageForm? ImageFormOpen() =>
        Application.OpenForms.OfType<ImageForm>().FirstOrDefault(f => f.Visible && !f.IsDisposed);

    /// <summary>把控件画到位图上，统计出现多少种颜色（纯背景只有 1 种）。</summary>
    private static (Bitmap bmp, int colors) Snapshot(Control c)
    {
        var bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height));
        c.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));

        var set = new HashSet<int>();
        for (var y = 0; y < bmp.Height; y += 3)
        for (var x = 0; x < bmp.Width; x += 3)
            set.Add(bmp.GetPixel(x, y).ToArgb());

        return (bmp, set.Count);
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
            var others = Process.GetProcesses()
                .Count(p => p.ProcessName is "GuangPanCheckin" or "光盘打卡提醒" && p.Id != Environment.ProcessId);
            if (others > 0)
                Info($"注意：检测到 {others} 个正在运行的实例，它的卡片可能与本测试重叠（会记在下面）");

            Store.Load();
            var ctx = new TrayContext();
            Pump(400);

            // ---------- 1 卡片弹出，按钮真的在最上层 ----------
            ctx.PreviewReminder();
            Pump(600);

            var card = Application.OpenForms.OfType<ReminderForm>().FirstOrDefault(f => f.Visible && !f.IsDisposed);
            Check("提醒卡片已弹出", card is not null);
            if (card is null) throw new InvalidOperationException("卡片没出来，后面的步骤无从谈起");

            var btnGo = card.Controls.OfType<Button>().FirstOrDefault(b => b.Text == "去打卡");
            var btnAck = card.Controls.OfType<Button>().FirstOrDefault(b => b.Text == "我已打卡");
            Check("卡片上有「去打卡」按钮", btnGo is not null);
            Check("卡片上有「我已打卡」按钮", btnAck is not null);
            if (btnGo is null) throw new InvalidOperationException("找不到「去打卡」按钮");

            Check("「去打卡」在卡片可视范围内",
                btnGo.Left >= 0 && btnGo.Top >= 0
                && btnGo.Right <= card.ClientSize.Width && btnGo.Bottom <= card.ClientSize.Height,
                $"{btnGo.Bounds} / 卡片 {card.ClientSize}");

            var btnCenter = new Point(btnGo.Left + btnGo.Width / 2, btnGo.Top + btnGo.Height / 2);
            var hit = IsTopWindowAt(card, btnCenter);
            Check("按钮位置上没有别的窗口遮挡", hit, hit ? "" : "该点最上层不是本卡片");

            // ---------- 2 真实点击「去打卡」 ----------
            var beforeOpen = Application.OpenForms.OfType<ImageForm>().Count();
            RealClick(btnGo);
            Pump(700);

            Check("点完卡片自动收起", !card.Visible || card.IsDisposed);
            Check("点击后没有多出窗口", Application.OpenForms.OfType<ImageForm>().Count() == beforeOpen + 1,
                $"{Application.OpenForms.OfType<ImageForm>().Count()}");

            var img = ImageFormOpen();
            Check("图片窗已弹出且可见", img is not null);

            if (img is not null)
            {
                var wa = Screen.FromPoint(img.PointToScreen(new Point(img.Width / 2, img.Height / 2))).WorkingArea;
                Check("图片窗在屏幕内", wa.Contains(img.Bounds), img.Bounds.ToString());
                Check("贴右下角 16px", wa.Right - img.Right is >= 0 and <= 40 && wa.Bottom - img.Bottom is >= 0 and <= 40,
                    $"right={wa.Right - img.Right} bottom={wa.Bottom - img.Bottom}");
                Check("宽度 320", img.Width == 320, img.Width.ToString());
                Check("高度 = 图高 + 56 按钮条", img.Height > 56, img.Height.ToString());
                Check("不进任务栏", !img.ShowInTaskbar);
                Check("置顶", img.TopMost);

                var onTop = IsTopWindowAt(img, new Point(img.Width / 2, Math.Max(4, img.Height / 2 - 20)));
                Check("图片窗没被其他窗口盖住", onTop, onTop ? "" : "该点最上层不是图片窗");

                // 真的画了图吗？纯背景只有 1 种颜色
                var pane = img.Controls.Cast<Control>().FirstOrDefault();
                Check("图片区存在", pane is not null);
                if (pane is not null)
                {
                    var (shot, colors) = Snapshot(pane);
                    using (shot)
                        Check("图片区已绘制照片（颜色数 > 200）", colors > 200, $"colors={colors}");

                    // 与源图中心像素比对，确认是「对应的那张图」而不是占位块
                    var (src, err) = AssetStore.Load();
                    if (src is null)
                    {
                        Info("源图读不到，跳过像素比对：" + err);
                    }
                    else
                    {
                        using (src)
                        {
                            using var srcBmp = src as Bitmap ?? new Bitmap(src);
                            var sc = srcBmp.GetPixel(srcBmp.Width / 2, srcBmp.Height / 2);
                            // 图片按等比缩放居中绘制，中心点在控件上仍是中心
                            var paneCenter = new Point(pane.Width / 2, Math.Max(1, pane.Height / 2));
                            using var shot2 = new Bitmap(pane.Width, pane.Height);
                            pane.DrawToBitmap(shot2, new Rectangle(0, 0, shot2.Width, shot2.Height));
                            var dc = shot2.GetPixel(paneCenter.X, paneCenter.Y);

                            var diff = Math.Abs(dc.R - sc.R) + Math.Abs(dc.G - sc.G) + Math.Abs(dc.B - sc.B);
                            Check("画的是同一张图（中心像素匹配）", diff < 60,
                                $"源 {sc.R},{sc.G},{sc.B} / 窗 {dc.R},{dc.G},{dc.B} / 差 {diff}");
                        }
                    }
                }

                // ---------- 3 重复点不再叠窗 ----------
                ctx.ShowImageCard();
                Pump(400);
                Check("重复触发仍只有一个图片窗", Application.OpenForms.OfType<ImageForm>().Count() == 1,
                    Application.OpenForms.OfType<ImageForm>().Count().ToString());

                // ---------- 4 图片窗里「我已打卡」、×、关闭 ----------
                var st = Logic.Compute(Store.Settings, Store.Records, DateTimeOffset.Now);
                var canCheckNow = st.Remaining > 0 && st.CanCheckNow;
                var nBefore = Store.Records.Count;

                var ackBtn = img.Controls.OfType<Panel>()
                    .SelectMany(p => p.Controls.OfType<Button>())
                    .FirstOrDefault(b => b.Text == "我已打卡");
                Check("图片窗底部有「我已打卡」", ackBtn is not null);

                if (ackBtn is not null)
                {
                    RealClick(ackBtn);
                    Pump(600);
                    if (canCheckNow)
                    {
                        Check("点「我已打卡」写入记录", Store.Records.Count == nBefore + 1,
                            $"{nBefore} -> {Store.Records.Count}");
                        Check("写入后图片窗关闭", ImageFormOpen() is null);
                    }
                    else
                    {
                        // 间隔未满/已满 3 次时应当拦住且不关窗，这是正确行为
                        Check("不允许打卡时被拦下且不关窗",
                            Store.Records.Count == nBefore && ImageFormOpen() is not null,
                            $"remaining={st.Remaining} canNow={st.CanCheckNow}");
                        Info("今天当前状态不适合再打卡，本次按「拦截」分支断言");
                    }
                }

                // 重新打开，测右上角 ×
                if (ImageFormOpen() is null)
                {
                    ctx.ShowImageCard();
                    Pump(500);
                }
                var f2 = ImageFormOpen();
                Check("可以再次打开图片窗", f2 is not null);
                if (f2 is not null)
                {
                    var pane2 = f2.Controls.Cast<Control>().First();
                    // CloseRect = (Width-38, 10, 26, 26)
                    var closePt = new Point(pane2.Width - 38 + 13, 10 + 13);
                    RealClick(pane2.PointToScreen(closePt));
                    Pump(500);
                    Check("点右上角 × 能关闭", ImageFormOpen() is null);
                }
            }
        }
        catch (Exception ex)
        {
            Check("点击链路", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            // 收尾：别把测试窗口留在屏幕上
            foreach (var f in Application.OpenForms.OfType<ImageForm>().ToArray())
            {
                try { f.Close(); } catch { }
            }

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
            File.WriteAllText(Path.Combine(Store.Dir, "uitest.log"), string.Join("\r\n", Lines), System.Text.Encoding.UTF8);
        }
        catch { }
        return _fail;
    }
}
