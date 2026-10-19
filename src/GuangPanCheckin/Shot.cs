using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace GuangPanCheckin;

/// <summary>把自己渲染成 PNG，仅用于开发期检查排版。</summary>
internal static class Shot
{
    public static void Run()
    {
        Store.Load();

        // 造两条演示数据，只为看界面
        var now = DateTimeOffset.Now;
        Store.Records.Clear();
        Store.Records.Add(new CheckRecord { Ts = now.Date.AddHours(7).AddMinutes(31), Date = DateTime.Now.ToString("yyyy-MM-dd") });
        Store.Records.Add(new CheckRecord { Ts = now.Date.AddHours(12).AddMinutes(6), Date = DateTime.Now.ToString("yyyy-MM-dd") });
        var y = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
        Store.Records.Add(new CheckRecord { Ts = DateTime.Now.AddDays(-1).Date.AddHours(7).AddMinutes(40), Date = y });
        Store.Records.Add(new CheckRecord { Ts = DateTime.Now.AddDays(-1).Date.AddHours(12).AddMinutes(2), Date = y });
        Store.Records.Add(new CheckRecord { Ts = DateTime.Now.AddDays(-1).Date.AddHours(18).AddMinutes(20), Date = y });

        Capture(new MainForm(null), "shot-main.png");
        Capture(new SettingsForm(null), "shot-settings.png");
        Capture(new ReminderForm(), "shot-reminder.png", r =>
            ((ReminderForm)r).Present(1, Logic.Compute(Store.Settings, Store.Records, DateTimeOffset.Now), Store.Settings));

        var (img, err) = AssetStore.Load();
        if (img is not null) Capture(new ImageForm(img), "shot-image.png");
        else AppLog.Warn("截图跳过示意图窗口: " + err);
    }

    private static void Capture(Form f, string file, Action<Form>? prepare = null)
    {
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-3000, -3000);
        f.Show();
        prepare?.Invoke(f);
        Application.DoEvents();
        Thread.Sleep(150);
        Application.DoEvents();

        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(Path.Combine(Store.Dir, file), ImageFormat.Png);
        f.Hide();
        f.Dispose();
    }
}
