using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GuangPanCheckin;

public static class Theme
{
    public static readonly Color Bg = FromHex("#11151A");
    public static readonly Color Card = FromHex("#1A2028");
    public static readonly Color Card2 = FromHex("#202833");
    public static readonly Color Border = FromHex("#2A3542");
    public static readonly Color Text = FromHex("#E8EDF3");
    public static readonly Color Muted = FromHex("#8D9AA8");
    public static readonly Color Accent = FromHex("#34D399");
    public static readonly Color AccentDark = FromHex("#12674A");
    public static readonly Color Warn = FromHex("#FBBF24");
    public static readonly Color Danger = FromHex("#F87171");

    public static Color FromHex(string hex) => ColorTranslator.FromHtml(hex);

    public static Font F(float size, FontStyle style = FontStyle.Regular)
        => new("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
}

/// <summary>圆角卡片面板。</summary>
public sealed class RoundPanel : Panel
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 14;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; } = Theme.Card;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Stroke { get; set; } = Theme.Border;

    public RoundPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = Round(rect, Radius);
        using var brush = new SolidBrush(Fill);
        e.Graphics.FillPath(brush, path);
        using var pen = new Pen(Stroke, 1f);
        e.Graphics.DrawPath(pen, path);
    }

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1, radius * 2);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

public static class UI
{
    public static Button Flat(string text, Color back, Color fore, float size = 10f)
    {
        var b = new Button
        {
            Text = text,
            BackColor = back,
            ForeColor = fore,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.F(size, FontStyle.Regular),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Lighten(back, 18);
        b.FlatAppearance.MouseDownBackColor = Lighten(back, 34);
        return b;
    }

    public static Color Lighten(Color c, int delta)
        => Color.FromArgb(
            Math.Min(255, c.R + delta),
            Math.Min(255, c.G + delta),
            Math.Min(255, c.B + delta));

    public static Label Lbl(string text, float size, Color color, bool bold = false)
        => new()
        {
            Text = text,
            Font = Theme.F(size, bold ? FontStyle.Bold : FontStyle.Regular),
            ForeColor = color,
            BackColor = Color.Transparent,
            AutoSize = true
        };

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>应用统一深色标题栏（标题栏跟随系统深色）。</summary>
    public static void DarkTitleBar(Form f)
    {
        f.BackColor = Theme.Bg;
        f.ForeColor = Theme.Text;
        f.FormBorderStyle = FormBorderStyle.FixedSingle;
        f.MaximizeBox = false;
        f.StartPosition = FormStartPosition.CenterScreen;
        f.HandleCreated += (_, _) => ApplyDarkTitle(f);
    }

    private static void ApplyDarkTitle(Form f)
    {
        try
        {
            var on = 1;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int));
        }
        catch { }
    }
}
