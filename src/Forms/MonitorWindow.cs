using System;
using System.Drawing;
using System.Windows.Forms;
using RemoteDesktop.Core;

namespace RemoteDesktop.Forms;

/// <summary>
/// One window per remote monitor. Supports fullscreen (F11), two scale modes,
/// and pauses the stream when minimised to save bandwidth.
/// </summary>
public sealed class MonitorWindow : Form
{
    // ── Public ───────────────────────────────────────────────────────────────
    public event Action? RequestDisconnect;

    // ── Private fields ────────────────────────────────────────────────────────
    private readonly MasterClient _client;
    private readonly int          _monitorIndex;
    private readonly PictureBox   _pb;
    private readonly Panel        _overlay;          // fullscreen auto-hide toolbar
    private readonly Label        _overlayTitle;
    private readonly Button       _overlayScale;
    private readonly Button       _overlayFullscreen;
    private readonly System.Windows.Forms.Timer _overlayTimer;

    private bool            _fullscreen;
    private FormBorderStyle _prevBorder;
    private Rectangle       _prevBounds;
    private PictureBoxSizeMode _scaleMode = PictureBoxSizeMode.Zoom;
    private int             _overlayCountdown;
    private bool            _streamPaused;
    private readonly string _baseTitle;
    private readonly Panel  _toolbar;
    private readonly Label  _toolbarTitle;
    private readonly Button _toolbarScale;

    // ── Constructor ───────────────────────────────────────────────────────────
    public MonitorWindow(MasterClient client, int monitorIndex, string title)
    {
        _client       = client;
        _monitorIndex = monitorIndex;
        _baseTitle    = title;
        Text          = title;

        Size          = new Size(960, 580);
        MinimumSize   = new Size(320, 200);
        StartPosition = FormStartPosition.Manual;
        BackColor     = Color.Black;
        KeyPreview    = true;

        // ── PictureBox ──
        _pb = new PictureBox
        {
            Dock      = DockStyle.Fill,
            SizeMode  = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
            Cursor    = Cursors.Cross,
            TabStop   = true,
        };
        _pb.MouseMove  += (_, e) => { OnPbMouseMove(e);  CheckOverlayTrigger(e.Y); };
        _pb.MouseDown  += (_, e) => { _pb.Focus(); OnPbMouseDown(e); };
        _pb.MouseUp    += (_, e) => OnPbMouseUp(e);
        Controls.Add(_pb);

        // ── Toolbar (normal mode) ──
        _toolbar = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(45, 45, 48) };
        _toolbarTitle = new Label
        {
            Text      = title,
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 9, FontStyle.Bold),
            Location  = new Point(8, 9),
            Size      = new Size(400, 18),
            BackColor = Color.Transparent,
        };
        _toolbarScale = new Button
        {
            Text      = "等比例",
            Size      = new Size(68, 28),
            Location  = new Point(724, 4),
            Anchor    = AnchorStyles.Top | AnchorStyles.Right,
            Font      = new Font("Segoe UI", 9),
            BackColor = Color.FromArgb(70, 70, 70),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
        };
        var tbFs = new Button
        {
            Text      = "全螢幕 F11",
            Size      = new Size(92, 28),
            Location  = new Point(800, 4),
            Anchor    = AnchorStyles.Top | AnchorStyles.Right,
            Font      = new Font("Segoe UI", 9),
            BackColor = Color.FromArgb(70, 70, 70),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
        };
        var tbDisc = new Button
        {
            Text      = "中斷連線",
            Size      = new Size(80, 28),
            Location  = new Point(900, 4),
            Anchor    = AnchorStyles.Top | AnchorStyles.Right,
            Font      = new Font("Segoe UI", 9),
            BackColor = Color.FromArgb(160, 50, 50),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
        };
        _toolbarScale.Click += (_, _) => ToggleScale();
        tbFs.Click          += (_, _) => ToggleFullscreen();
        tbDisc.Click        += (_, _) => RequestDisconnect?.Invoke();
        _toolbar.Controls.AddRange(new Control[] { _toolbarTitle, _toolbarScale, tbFs, tbDisc });
        Controls.Add(_toolbar);

        // ── Overlay toolbar (fullscreen only) ──
        _overlay = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 44,
            BackColor = Color.FromArgb(230, 28, 28, 28),
            Visible   = false,
        };

        _overlayTitle = new Label
        {
            Text      = title,
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 10, FontStyle.Bold),
            Location  = new Point(12, 12),
            Size      = new Size(400, 22),
            BackColor = Color.Transparent,
        };

        _overlayScale = MakeOverlayButton("等比例", 420);
        _overlayScale.Click += (_, _) => ToggleScale();

        _overlayFullscreen = MakeOverlayButton("離開全螢幕  Esc", 530);
        _overlayFullscreen.Click += (_, _) => ExitFullscreen();

        var overlayDisconnect = MakeOverlayButton("中斷連線", 700);
        overlayDisconnect.BackColor = Color.FromArgb(160, 50, 50);
        overlayDisconnect.Click += (_, _) => RequestDisconnect?.Invoke();

        _overlay.Controls.AddRange(new Control[]
            { _overlayTitle, _overlayScale, _overlayFullscreen, overlayDisconnect });
        Controls.Add(_overlay);
        _overlay.BringToFront();

        // Overlay timer – checks mouse position every 100 ms
        _overlayTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _overlayTimer.Tick += OnOverlayTimer;

        // Right-click forwards to remote — no local context menu on _pb

        // ── Keyboard ──
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F11) { ToggleFullscreen(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Escape && _fullscreen) { ExitFullscreen(); e.Handled = true; return; }
            _client.SendKeyEvent((ushort)e.KeyCode, true);
            e.Handled = e.SuppressKeyPress = true;
        };
        KeyUp += (_, e) => _client.SendKeyEvent((ushort)e.KeyCode, false);

        // ── Mouse wheel (forwarded at form level) ──
        MouseWheel += (_, e) =>
        {
            var pos = _pb.PointToClient(Cursor.Position);
            var (nx, ny) = Normalize(pos.X, pos.Y);
            _client.SendMouseScroll(_monitorIndex, nx, ny, e.Delta);
        };
    }

    // ── Public: receive a new frame ──────────────────────────────────────────
    public void UpdateImage(Image img)
    {
        if (IsDisposed) { img.Dispose(); return; }
        try
        {
            Invoke(() =>
            {
                var old = _pb.Image;
                _pb.Image = img;
                old?.Dispose();
            });
        }
        catch { img.Dispose(); }
    }

    // ── Fullscreen ────────────────────────────────────────────────────────────
    private void ToggleFullscreen()
    {
        if (_fullscreen) ExitFullscreen(); else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        _prevBorder = FormBorderStyle;
        _prevBounds = Bounds;

        var screen = Screen.FromControl(this);
        FormBorderStyle = FormBorderStyle.None;
        WindowState     = FormWindowState.Normal;
        Bounds          = screen.Bounds;
        TopMost         = true;
        _fullscreen     = true;
        _toolbar.Visible = false;

        _overlayTimer.Start();
        UpdateTitle();
    }

    private void ExitFullscreen()
    {
        _overlayTimer.Stop();
        _overlay.Visible = false;
        TopMost          = false;
        FormBorderStyle  = _prevBorder;
        Bounds           = _prevBounds;
        _fullscreen      = false;
        _toolbar.Visible = true;
        UpdateTitle();
    }

    // Overlay auto-show/hide
    private void CheckOverlayTrigger(int pbY)
    {
        if (!_fullscreen) return;
        if (pbY < 8) { ShowOverlay(); }
    }

    private void ShowOverlay()
    {
        _overlay.Visible    = true;
        _overlayCountdown   = 25; // 25 × 100 ms = 2.5 s
    }

    private void OnOverlayTimer(object? s, EventArgs e)
    {
        if (!_fullscreen) return;
        if (!_overlay.Visible) return;

        // Keep visible if mouse is over it
        var mp = PointToClient(Cursor.Position);
        if (mp.Y >= 0 && mp.Y <= _overlay.Height)
        {
            _overlayCountdown = 25;
            return;
        }
        if (--_overlayCountdown <= 0)
            _overlay.Visible = false;
    }

    // ── Scale ─────────────────────────────────────────────────────────────────
    private void ToggleScale()
    {
        _scaleMode = _scaleMode == PictureBoxSizeMode.Zoom
            ? PictureBoxSizeMode.StretchImage
            : PictureBoxSizeMode.Zoom;
        _pb.SizeMode         = _scaleMode;
        _overlayScale.Text   = _scaleMode == PictureBoxSizeMode.Zoom ? "等比例" : "填滿";
        _toolbarScale.Text   = _overlayScale.Text;
        UpdateTitle();
    }

    private void SetScale(PictureBoxSizeMode mode, ToolStripMenuItem zoom, ToolStripMenuItem stretch)
    {
        _scaleMode        = mode;
        _pb.SizeMode      = mode;
        zoom.Checked      = mode == PictureBoxSizeMode.Zoom;
        stretch.Checked   = mode == PictureBoxSizeMode.StretchImage;
        _overlayScale.Text = mode == PictureBoxSizeMode.Zoom ? "等比例" : "填滿";
        _toolbarScale.Text = _overlayScale.Text;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        string scale = _scaleMode == PictureBoxSizeMode.Zoom ? "等比例" : "填滿";
        string fs    = _fullscreen ? "  [全螢幕]" : "";
        Text = $"{_baseTitle}  [{scale}]{fs}";
        _overlayTitle.Text = _baseTitle;
    }

    // ── Mouse forwarding ──────────────────────────────────────────────────────
    private void OnPbMouseMove(MouseEventArgs e)
    {
        var (nx, ny) = Normalize(e.X, e.Y);
        _client.SendMouseMove(_monitorIndex, nx, ny);
    }

    private void OnPbMouseDown(MouseEventArgs e)
    {
        var (nx, ny) = Normalize(e.X, e.Y);
        int btn = e.Button == MouseButtons.Right ? 1 : e.Button == MouseButtons.Middle ? 2 : 0;
        _client.SendMouseClick(_monitorIndex, nx, ny, btn, true);
    }

    private void OnPbMouseUp(MouseEventArgs e)
    {
        var (nx, ny) = Normalize(e.X, e.Y);
        int btn = e.Button == MouseButtons.Right ? 1 : e.Button == MouseButtons.Middle ? 2 : 0;
        _client.SendMouseClick(_monitorIndex, nx, ny, btn, false);
    }

    // Zoom-aware coordinate normalisation
    private (float nx, float ny) Normalize(int mx, int my)
    {
        if (_pb.Image == null) return (0, 0);
        float iw = _pb.Image.Width, ih = _pb.Image.Height;
        float bw = _pb.Width,       bh = _pb.Height;

        float nx, ny;
        if (_scaleMode == PictureBoxSizeMode.StretchImage)
        {
            nx = (float)mx / bw;
            ny = (float)my / bh;
        }
        else // Zoom – letterbox
        {
            float scale, ox, oy;
            if (iw / ih > bw / bh) { scale = bw / iw; ox = 0;                   oy = (bh - ih * scale) / 2; }
            else                   { scale = bh / ih; ox = (bw - iw * scale) / 2; oy = 0; }
            nx = (mx - ox) / (iw * scale);
            ny = (my - oy) / (ih * scale);
        }
        return (Math.Clamp(nx, 0, 1), Math.Clamp(ny, 0, 1));
    }

    // ── Pause on minimise ─────────────────────────────────────────────────────
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        bool shouldPause = WindowState == FormWindowState.Minimized;
        if (shouldPause == _streamPaused) return;
        _streamPaused = shouldPause;
        if (shouldPause) _client.SendStreamPause(_monitorIndex);
        else             _client.SendStreamResume(_monitorIndex);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _overlayTimer.Stop();
        if (_fullscreen) ExitFullscreen();
        base.OnFormClosing(e);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static Button MakeOverlayButton(string text, int x) => new()
    {
        Text      = text,
        Location  = new Point(x, 8),
        Size      = new Size(text.Length * 8 + 20, 28),
        Font      = new Font("Segoe UI", 9),
        BackColor = Color.FromArgb(70, 70, 70),
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        Cursor    = Cursors.Hand,
    };

    public static string MakeTitle(string nickname, int index, int total) =>
        total == 1 ? nickname :
        total == 2 ? $"{nickname} — {(index == 0 ? "左螢幕" : "右螢幕")}" :
        $"{nickname} — 螢幕 {index + 1}";
}
