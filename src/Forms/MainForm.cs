using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using RemoteDesktop.Core;

namespace RemoteDesktop.Forms;

public sealed class MainForm : Form
{
    // ── Connection tracking ───────────────────────────────────────────────────
    private sealed class Connection
    {
        public string          Nickname    = "";
        public string          Ip          = "";
        public MasterClient    Client      = null!;
        public MonitorWindow[] Windows     = Array.Empty<MonitorWindow>();
        public ListViewItem?   Item;
        public int             OpenWindows;   // decremented on each FormClosed; reaches 0 → disconnect
    }

    private readonly List<Connection> _connections = new();

    // ── Controls ──────────────────────────────────────────────────────────────
    private readonly ListView  _listView;
    private readonly Button    _btnAdd;
    private readonly Button    _btnDisconnect;
    private readonly Button    _btnShow;
    private readonly Button    _btnSlave;
    private readonly Label     _hintLabel;

    public MainForm()
    {
        Text            = $"Remote Desktop v{AppInfo.Version} — 連線管理";
        Size            = new Size(480, 400);
        MinimumSize     = new Size(420, 340);
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.FromArgb(30, 30, 30);

        // ── Title label ──
        var title = new Label
        {
            Text      = "Remote Desktop",
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 15, FontStyle.Bold),
            Location  = new Point(20, 16),
            Size      = new Size(300, 32),
            BackColor = Color.Transparent,
        };

        // ── ListView ──
        _listView = new ListView
        {
            Location      = new Point(20, 60),
            Size          = new Size(430, 210),
            View          = View.Details,
            FullRowSelect = true,
            GridLines     = true,
            BackColor     = Color.FromArgb(45, 45, 48),
            ForeColor     = Color.White,
            Font          = new Font("Segoe UI", 10),
            BorderStyle   = BorderStyle.FixedSingle,
        };
        _listView.Columns.Add("狀態",  52);
        _listView.Columns.Add("暱稱", 140);
        _listView.Columns.Add("IP",   145);
        _listView.Columns.Add("螢幕",  60);
        _listView.SelectedIndexChanged += (_, _) => UpdateButtons();
        _listView.DoubleClick += (_, _) => BringWindowsToFront();

        // ── Buttons ──
        _btnAdd        = MakeBtn("＋ 新增連線", new Point(20, 282),  150, Color.FromArgb(0, 120, 215));
        _btnDisconnect = MakeBtn("✕ 中斷連線",  new Point(178, 282), 130, Color.FromArgb(160, 50, 50));
        _btnShow       = MakeBtn("↗ 顯示視窗",  new Point(316, 282), 134, Color.FromArgb(70, 70, 70));

        _btnAdd.Click        += (_, _) => AddConnection();
        _btnDisconnect.Click += (_, _) => DisconnectSelected();
        _btnShow.Click       += (_, _) => BringWindowsToFront();

        // ── Slave button ──
        var sep = new Label
        {
            Location  = new Point(20, 328),
            Size      = new Size(430, 1),
            BackColor = Color.FromArgb(60, 60, 60),
        };
        _btnSlave = MakeBtn("開啟被控端 (Slave)", new Point(20, 338), 180, Color.FromArgb(60, 100, 60));
        _btnSlave.Click += (_, _) => OpenSlave();

        _hintLabel = new Label
        {
            Text      = "雙擊列表可將視窗帶到前景",
            ForeColor = Color.FromArgb(90, 90, 90),
            Font      = new Font("Segoe UI", 8),
            Location  = new Point(210, 346),
            Size      = new Size(240, 20),
            BackColor = Color.Transparent,
        };

        Controls.AddRange(new Control[]
            { title, _listView, _btnAdd, _btnDisconnect, _btnShow, sep, _btnSlave, _hintLabel });

        UpdateButtons();
    }

    // ── Add connection ────────────────────────────────────────────────────────
    private void AddConnection()
    {
        using var dlg = new ConnectDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var conn = new Connection { Nickname = dlg.Nickname, Ip = dlg.Ip };
        var item = new ListViewItem(new[] { "⏳", dlg.Nickname, dlg.Ip, "…" })
            { ForeColor = Color.Gray };
        conn.Item = item;
        _connections.Add(conn);
        _listView.Items.Add(item);

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var client = new MasterClient();
            conn.Client = client;

            client.StatusChanged += _ => { };
            client.Disconnected  += () => SafeInvoke(() => OnDisconnected(conn));

            client.MonitorInfoReceived += (ws, hs) =>
                SafeInvoke(() => CreateMonitorWindows(conn, ws, hs));

            client.ScreenUpdated += (idx, img) =>
            {
                if (idx < conn.Windows.Length)
                    conn.Windows[idx].UpdateImage(img);
                else
                    img.Dispose();
            };

            bool ok = client.Connect(dlg.Ip, dlg.Pin, dlg.Port);
            SafeInvoke(() =>
            {
                if (!ok)
                {
                    _connections.Remove(conn);
                    _listView.Items.Remove(item);
                    MessageBox.Show($"無法連線到 {dlg.Ip}\n請確認 IP、PIN 和防火牆設定。",
                        "連線失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (client.SlaveVersion != AppInfo.Version)
                {
                    var ans = MessageBox.Show(
                        $"版本不符：主控端 v{AppInfo.Version}，被控端 v{client.SlaveVersion}。\n確定繼續連線？",
                        "版本警告", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (ans == DialogResult.No) { Disconnect(conn); }
                }
            });
        });
    }

    private void CreateMonitorWindows(Connection conn, int[] ws, int[] hs)
    {
        int total = ws.Length;
        var wins  = new MonitorWindow[total];

        // Match remote monitors to local screens by left-to-right order
        var localScreens = Screen.AllScreens.OrderBy(s => s.Bounds.X).ToArray();

        const int ToolbarH = 36;

        for (int i = 0; i < total; i++)
        {
            int    idx   = i;                                  // captured by lambda (for-loop var is shared)
            string title = MonitorWindow.MakeTitle(conn.Nickname, idx, total);
            var    win   = new MonitorWindow(conn.Client, idx, title);

            // Place on corresponding local screen (by left-to-right index)
            var screen   = idx < localScreens.Length ? localScreens[idx] : Screen.PrimaryScreen!;
            var workArea = screen.WorkingArea;

            // Size window so image area matches remote monitor aspect ratio
            float aspect  = (float)ws[idx] / hs[idx];
            int   chrome  = SystemInformation.CaptionHeight + SystemInformation.FrameBorderSize.Height * 2;
            int   maxImgW = workArea.Width  - 40;
            int   maxImgH = workArea.Height - 40 - chrome - ToolbarH;

            int imgW, imgH;
            if ((float)maxImgW / maxImgH >= aspect)
            {
                imgH = maxImgH;
                imgW = (int)(imgH * aspect);
            }
            else
            {
                imgW = maxImgW;
                imgH = (int)(imgW / aspect);
            }

            win.ClientSize    = new Size(imgW, imgH + ToolbarH);
            win.StartPosition = FormStartPosition.Manual;
            win.Location      = new Point(
                workArea.X + Math.Max(0, (workArea.Width  - win.Width)  / 2),
                workArea.Y + Math.Max(0, (workArea.Height - win.Height) / 2));

            win.RequestDisconnect += () => SafeInvoke(() => Disconnect(conn));
            win.FormClosed += (_, _) =>
            {
                try { conn.Client.SendStreamPause(idx); } catch { }
                // FormClosed fires BEFORE Dispose, so IsDisposed is still false here.
                // We use an explicit counter instead — when all windows closed, fully disconnect.
                SafeInvoke(() =>
                {
                    conn.OpenWindows--;
                    if (conn.OpenWindows <= 0 && _connections.Contains(conn))
                        Disconnect(conn);
                });
            };
            win.Show();
            wins[idx] = win;
        }
        conn.OpenWindows = total;

        conn.Windows = wins;
        conn.Item!.SubItems[0].Text = "●";
        conn.Item.SubItems[3].Text  = total.ToString();
        conn.Item.ForeColor         = Color.LimeGreen;
        UpdateButtons();
    }

    // ── Disconnect ────────────────────────────────────────────────────────────
    private void DisconnectSelected()
    {
        if (_listView.SelectedItems.Count == 0) return;
        var item = _listView.SelectedItems[0];
        var conn = _connections.Find(c => c.Item == item);
        if (conn != null) Disconnect(conn);
    }

    private void Disconnect(Connection conn)
    {
        foreach (var w in conn.Windows)
            if (!w.IsDisposed) w.Close();
        conn.Client?.Dispose();
        _connections.Remove(conn);
        if (conn.Item != null) _listView.Items.Remove(conn.Item);
        UpdateButtons();
    }

    private void OnDisconnected(Connection conn)
    {
        if (conn.Item == null) return;
        conn.Item.SubItems[0].Text = "✕";
        conn.Item.ForeColor        = Color.FromArgb(180, 50, 50);
        foreach (var w in conn.Windows)
            if (!w.IsDisposed) { w.Text += "  [已斷線]"; }
    }

    // ── Bring to front ────────────────────────────────────────────────────────
    private void BringWindowsToFront()
    {
        if (_listView.SelectedItems.Count == 0) return;
        var item = _listView.SelectedItems[0];
        var conn = _connections.Find(c => c.Item == item);
        if (conn == null) return;
        foreach (var w in conn.Windows)
        {
            if (w.IsDisposed) continue;
            if (w.WindowState == FormWindowState.Minimized)
                w.WindowState = FormWindowState.Normal;
            w.BringToFront();
            w.Activate();
        }
    }

    // ── Slave ─────────────────────────────────────────────────────────────────
    private void OpenSlave()
    {
        string colorPin = PinColors.Random();
        var form = new SlaveForm(colorPin);
        form.Show();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void UpdateButtons()
    {
        bool hasSel     = _listView.SelectedItems.Count > 0;
        _btnDisconnect.Enabled = hasSel;
        _btnShow.Enabled       = hasSel;
    }

    private void SafeInvoke(Action a)
    {
        if (IsDisposed) return;
        try { if (InvokeRequired) Invoke(a); else a(); } catch { }
    }

    private static Button MakeBtn(string text, Point loc, int width, Color back) => new()
    {
        Text      = text,
        Location  = loc,
        Size      = new Size(width, 34),
        Font      = new Font("Segoe UI", 10),
        BackColor = back,
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        Cursor    = Cursors.Hand,
    };
}

// ── PIN dialog (shared with SlaveForm) ────────────────────────────────────────
public sealed class PinDialog : Form
{
    public string Pin { get; private set; } = "";
    private readonly TextBox _box;

    public PinDialog(string caption, string okLabel)
    {
        Text            = caption;
        Size            = new Size(310, 175);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false; MinimizeBox = false;

        Controls.Add(new Label
        {
            Text = caption, Location = new Point(20, 22),
            Size = new Size(260, 26), Font = new Font("Segoe UI", 11),
        });

        _box = new TextBox
        {
            MaxLength = 4, Location = new Point(90, 56), Size = new Size(130, 32),
            Font = new Font("Segoe UI", 16), TextAlign = HorizontalAlignment.Center,
            PasswordChar = '●',
        };
        Controls.Add(_box);

        var ok = new Button
        {
            Text = okLabel, Location = new Point(85, 100), Size = new Size(140, 36),
            Font = new Font("Segoe UI", 10), DialogResult = DialogResult.None,
        };
        ok.Click += (_, _) =>
        {
            if (_box.Text.Length == 4 && int.TryParse(_box.Text, out _))
            {
                Pin = _box.Text; DialogResult = DialogResult.OK; Close();
            }
            else
                MessageBox.Show("請輸入 4 位數字。", "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        Controls.Add(ok);
        AcceptButton = ok;
    }
}
