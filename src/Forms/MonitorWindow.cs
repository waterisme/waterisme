using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using RemoteDesktop.Core;

namespace RemoteDesktop.Forms;

/// <summary>
/// One window per remote monitor. Always scales the received frame to fill the
/// canvas (letterbox / stretch selectable). Supports fullscreen (F11).
/// </summary>
public sealed class MonitorWindow : Form
{
    // ── Public ───────────────────────────────────────────────────────────────
    public event Action? RequestDisconnect;

    // ── Private fields ────────────────────────────────────────────────────────
    private readonly MasterClient _client;
    private readonly int          _monitorIndex;
    private readonly string       _baseTitle;

    // Canvas replaces PictureBox — we own the draw loop for DPI-safe scaling.
    // BufferedPanel enables OptimizedDoubleBuffer to prevent flicker.
    private readonly BufferedPanel _canvas;
    private Image?           _canvasImage;
    private readonly object  _imgLock     = new();
    private Rectangle        _imgDrawRect;          // updated on every paint
    private bool             _fillMode    = false;  // false = letterbox (aspect-correct)

    // Toolbars
    private readonly Panel   _toolbar;
    private readonly Label   _toolbarTitle;
    private readonly Button  _toolbarScaleBtn;

    private readonly Panel   _overlay;
    private readonly Label   _overlayTitle;
    private readonly Button  _overlayScaleBtn;
    private readonly System.Windows.Forms.Timer _overlayTimer;
    private int              _overlayCountdown;

    private bool             _fullscreen;
    private FormBorderStyle  _prevBorder;
    private Rectangle        _prevBounds;
    private bool             _streamPaused;

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

        // ── Canvas (custom-drawn, fills window below toolbar) ──
        _canvas = new BufferedPanel
        {
            Dock      = DockStyle.Fill,
            BackColor = Color.Black,
            Cursor    = Cursors.Cross,
            TabStop   = true,
        };
        _canvas.Paint     += OnCanvasPaint;
        _canvas.MouseMove += (_, e) => { OnCanvasMouseMove(e); CheckOverlayTrigger(e.Y); };
        _canvas.MouseDown += (_, e) => { _canvas.Focus(); OnCanvasMouseDown(e); };
        _canvas.MouseUp   += (_, e) => OnCanvasMouseUp(e);
        Controls.Add(_canvas);

        // ── Toolbar (normal mode) ──
        _toolbar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 36,
            BackColor = Color.FromArgb(45, 45, 48),
        };
        _toolbarTitle = new Label
        {
            Text      = title,
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 9, FontStyle.Bold),
            Location  = new Point(8, 9),
            Size      = new Size(400, 18),
            BackColor = Color.Transparent,
        };
        _toolbarScaleBtn = MakeToolbarButton("等比例", 724);
        var tbFs   = MakeToolbarButton("全螢幕 F11", 800);
        var tbDisc = MakeToolbarButton("中斷連線",   900);
        tbDisc.BackColor = Color.FromArgb(160, 50, 50);

        _toolbarScaleBtn.Click += (_, _) => ToggleScale();
        tbFs.Click             += (_, _) => ToggleFullscreen();
        tbDisc.Click           += (_, _) => RequestDisconnect?.Invoke();
        _toolbar.Controls.AddRange(new Control[] { _toolbarTitle, _toolbarScaleBtn, tbFs, tbDisc });
        Controls.Add(_toolbar);

        // ── Overlay toolbar (fullscreen auto-hide) ──
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
        _overlayScaleBtn = MakeOverlayButton("等比例", 420);
        var ovFs   = MakeOverlayButton("離開全螢幕  Esc", 530);
        var ovDisc = MakeOverlayButton("中斷連線", 700);
        ovDisc.BackColor = Color.FromArgb(160, 50, 50);

        _overlayScaleBtn.Click += (_, _) => ToggleScale();
        ovFs.Click             += (_, _) => ExitFullscreen();
        ovDisc.Click           += (_, _) => RequestDisconnect?.Invoke();
        _overlay.Controls.AddRange(new Control[] { _overlayTitle, _overlayScaleBtn, ovFs, ovDisc });
        Controls.Add(_overlay);
        _overlay.BringToFront();

        _overlayTimer          = new System.Windows.Forms.Timer { Interval = 100 };
        _overlayTimer.Tick    += OnOverlayTimer;

        // ── Keyboard ──
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F11)  { ToggleFullscreen(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Escape && _fullscreen) { ExitFullscreen(); e.Handled = true; return; }
            _client.SendKeyEvent((ushort)e.KeyCode, true);
            e.Handled = e.SuppressKeyPress = true;
        };
        KeyUp += (_, e) => _client.SendKeyEvent((ushort)e.KeyCode, false);

        // ── Mouse wheel ──
        MouseWheel += (_, e) =>
        {
            var pos = _canvas.PointToClient(Cursor.Position);
            var (nx, ny) = Normalize(pos.X, pos.Y);
            _client.SendMouseScroll(_monitorIndex, nx, ny, e.Delta);
        };
    }

    // ── Receive a new frame ──────────────────────────────────────────────────
    public void UpdateImage(Image img)
    {
        if (IsDisposed) { img.Dispose(); return; }
        try
        {
            Invoke(() =>
            {
                Image? old;
                lock (_imgLock) { old = _canvasImage; _canvasImage = img; }
                old?.Dispose();
                _canvas.Invalidate();
            });
        }
        catch { img.Dispose(); }
    }

    // ── Custom paint — always scales image to fill canvas ────────────────────
    private void OnCanvasPaint(object? s, PaintEventArgs e)
    {
        Image? img;
        lock (_imgLock) { img = _canvasImage; }
        if (img == null) return;

        e.Graphics.InterpolationMode = InterpolationMode.Bilinear;

        if (_fillMode)
        {
            // Stretch to fill — may distort if window aspect ≠ remote aspect
            var r = new Rectangle(0, 0, _canvas.Width, _canvas.Height);
            e.Graphics.DrawImage(img, r);
            _imgDrawRect = r;
        }
        else
        {
            // Letterbox fit — maintain remote aspect ratio
            float scale = Math.Min((float)_canvas.Width  / img.Width,
                                   (float)_canvas.Height / img.Height);
            int dw = (int)(img.Width  * scale);
            int dh = (int)(img.Height * scale);
            int dx = (_canvas.Width  - dw) / 2;
            int dy = (_canvas.Height - dh) / 2;
            e.Graphics.DrawImage(img, dx, dy, dw, dh);
            _imgDrawRect = new Rectangle(dx, dy, dw, dh);
        }
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
        var screen  = Screen.FromControl(this);
        FormBorderStyle  = FormBorderStyle.None;
        WindowState      = FormWindowState.Normal;
        Bounds           = screen.Bounds;
        TopMost          = true;
        _fullscreen      = true;
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

    private void CheckOverlayTrigger(int canvasY)
    {
        if (_fullscreen && canvasY < 8) ShowOverlay();
    }

    private void ShowOverlay()
    {
        _overlay.Visible  = true;
        _overlayCountdown = 25;   // 25 × 100 ms = 2.5 s
    }

    private void OnOverlayTimer(object? s, EventArgs e)
    {
        if (!_fullscreen || !_overlay.Visible) return;
        var mp = PointToClient(Cursor.Position);
        if (mp.Y >= 0 && mp.Y <= _overlay.Height) { _overlayCountdown = 25; return; }
        if (--_overlayCountdown <= 0) _overlay.Visible = false;
    }

    // ── Scale toggle ──────────────────────────────────────────────────────────
    private void ToggleScale()
    {
        _fillMode = !_fillMode;
        string label = _fillMode ? "填滿" : "等比例";
        _toolbarScaleBtn.Text = label;
        _overlayScaleBtn.Text = label;
        _canvas.Invalidate();
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        string scale = _fillMode ? "填滿" : "等比例";
        string fs    = _fullscreen ? "  [全螢幕]" : "";
        Text               = $"{_baseTitle}  [{scale}]{fs}";
        _overlayTitle.Text = _baseTitle;
    }

    // ── Mouse forwarding ──────────────────────────────────────────────────────
    private void OnCanvasMouseMove(MouseEventArgs e)
    {
        var (nx, ny) = Normalize(e.X, e.Y);
        _client.SendMouseMove(_monitorIndex, nx, ny);
    }

    private void OnCanvasMouseDown(MouseEventArgs e)
    {
        var (nx, ny) = Normalize(e.X, e.Y);
        int btn = e.Button == MouseButtons.Right ? 1 : e.Button == MouseButtons.Middle ? 2 : 0;
        _client.SendMouseClick(_monitorIndex, nx, ny, btn, true);
    }

    private void OnCanvasMouseUp(MouseEventArgs e)
    {
        var (nx, ny) = Normalize(e.X, e.Y);
        int btn = e.Button == MouseButtons.Right ? 1 : e.Button == MouseButtons.Middle ? 2 : 0;
        _client.SendMouseClick(_monitorIndex, nx, ny, btn, false);
    }

    /// <summary>
    /// Maps a canvas pixel position to a normalised (0–1, 0–1) coordinate
    /// relative to the remote screen, accounting for letterbox offset.
    /// </summary>
    private (float nx, float ny) Normalize(int mx, int my)
    {
        var r = _imgDrawRect;
        if (r.Width == 0 || r.Height == 0) return (0f, 0f);
        float nx = (float)(mx - r.X) / r.Width;
        float ny = (float)(my - r.Y) / r.Height;
        return (Math.Clamp(nx, 0f, 1f), Math.Clamp(ny, 0f, 1f));
    }

    // ── Pause stream on minimise ──────────────────────────────────────────────
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
        lock (_imgLock) { _canvasImage?.Dispose(); _canvasImage = null; }
        base.OnFormClosing(e);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static Button MakeToolbarButton(string text, int x) => new()
    {
        Text      = text,
        Location  = new Point(x, 4),
        Size      = new Size(text.Length * 9 + 16, 28),
        Anchor    = AnchorStyles.Top | AnchorStyles.Right,
        Font      = new Font("Segoe UI", 9),
        BackColor = Color.FromArgb(70, 70, 70),
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        Cursor    = Cursors.Hand,
    };

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

/// <summary>
/// Panel with OptimizedDoubleBuffer enabled — eliminates flicker when the
/// remote frame is redrawn 30× per second.
/// </summary>
internal sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint
               | ControlStyles.ResizeRedraw, true);
        UpdateStyles();
    }
}
