using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GuangPanCheckin;

/// <summary>
/// 「去打卡」图片窗：无边框、置顶、不抢焦点、可拖动、不进 Alt+Tab。
/// 底部带「我已打卡」和「关闭」，右上角有 ×。
/// </summary>
public sealed class ImageForm : Form
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr TopMostFlag = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    public event Action? AckCheckin;

    private const int PaneWidth = 320;
    private const int BarHeight = 56;

    private readonly ImagePane _pane;
    private readonly Panel _bar;
    private bool _dragging;
    private bool _closing;
    private Point _dragOffset;

    public ImageForm(Image img)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Theme.Card;
        DoubleBuffered = true;
        KeyPreview = true;

        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var maxH = Math.Max(160, (int)(wa.Height * 0.8));
        var scale = Math.Min((double)PaneWidth / Math.Max(1, img.Width), (double)maxH / Math.Max(1, img.Height));

        var w = Math.Max(1, (int)Math.Round(img.Width * scale));
        var h = Math.Max(1, (int)Math.Round(img.Height * scale));

        ClientSize = new Size(PaneWidth, h + BarHeight);

        _pane = new ImagePane(img, new Rectangle((PaneWidth - w) / 2, 0, w, h))
        {
            Bounds = new Rectangle(0, 0, PaneWidth, h)
        };
        _pane.MouseDown += OnPaneDown;
        _pane.MouseMove += OnPaneMove;
        _pane.MouseUp += (_, _) => _dragging = false;

        _bar = new Panel { Bounds = new Rectangle(0, h, PaneWidth, BarHeight), BackColor = Theme.Card };
        _bar.MouseDown += OnDragStart;
        _bar.MouseMove += OnDragMove;
        _bar.MouseUp += (_, _) => _dragging = false;

        var btnAck = UI.Flat("我已打卡", Theme.Accent, Theme.FromHex("#06281C"), 10f);
        btnAck.SetBounds(12, 9, 152, 38);
        btnAck.Click += (_, _) => AckCheckin?.Invoke();

        var btnClose = UI.Flat("关闭", Theme.Card2, Theme.Muted, 9.5f);
        btnClose.SetBounds(176, 9, 132, 38);
        btnClose.Click += (_, _) => Close();

        _bar.Controls.AddRange(new Control[] { btnAck, btnClose });
        Controls.AddRange(new Control[] { _pane, _bar });

        MouseDown += OnDragStart;
        MouseMove += OnDragMove;
        MouseUp += (_, _) => _dragging = false;

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };

        ApplyCorners();
    }

    public void ShowAtBottomRight()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        if (!Visible) Show();
        ForceTopMost();
    }

    /// <summary>已经开着时再点「去打卡」，叫到最前面而不是再开一个。</summary>
    public void Nudge()
    {
        if (!Visible) Show();
        ForceTopMost();
        try { Activate(); } catch { }
    }

    /// <summary>TopMost 属性在「不带焦点显示」时偶尔不生效，直接用 Win32 顶一次。</summary>
    private void ForceTopMost()
    {
        try
        {
            if (Handle != IntPtr.Zero)
                SetWindowPos(Handle, TopMostFlag, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch { }
        BringToFront();
    }

    private void OnPaneDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_pane.CloseRect.Contains(e.Location))
        {
            Close();
            return;
        }
        OnDragStart(sender, e);
    }

    private void OnPaneMove(object? sender, MouseEventArgs e) => OnDragMove(sender, e);

    private void OnDragStart(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        var p = PointToClient(Cursor.Position);
        _dragOffset = new Point(p.X - Left, p.Y - Top);
    }

    private void OnDragMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = Cursor.Position;
        Location = new Point(p.X - _dragOffset.X, p.Y - _dragOffset.Y);
    }

    private void ApplyCorners()
    {
        using var path = RoundPanel.Round(new Rectangle(0, 0, Width, Height), 14);
        Region = new Region(path);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW：不进 Alt+Tab
            return cp;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closing)
        {
            base.OnFormClosing(e);
            return;
        }
        _closing = true;
        _pane.ReleaseImage();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pane.ReleaseImage();
        base.Dispose(disposing);
    }

    /// <summary>图片绘制区：自己画图 + 右上角关闭徽标（避免半透明控件叠图的坑）。</summary>
    private sealed class ImagePane : Control
    {
        private readonly Rectangle _target;
        private Image? _img;

        public ImagePane(Image img, Rectangle target)
        {
            _img = img;
            _target = target;
            BackColor = Theme.Bg;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }

        public Rectangle CloseRect => new(Width - 38, 10, 26, 26);

        public void ReleaseImage()
        {
            var img = _img;
            _img = null;
            img?.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            if (_img is not null) g.DrawImage(_img, _target);

            var r = CloseRect;
            using (var back = new SolidBrush(Color.FromArgb(150, 14, 18, 22)))
                g.FillEllipse(back, r);

            using var pen = new Pen(Color.FromArgb(235, 240, 245, 250), 1.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            var pad = 8;
            g.DrawLine(pen, r.Left + pad, r.Top + pad, r.Right - pad, r.Bottom - pad);
            g.DrawLine(pen, r.Right - pad, r.Top + pad, r.Left + pad, r.Bottom - pad);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = CloseRect.Contains(e.Location) ? Cursors.Hand : Cursors.SizeAll;
            base.OnMouseMove(e);
        }
    }
}
