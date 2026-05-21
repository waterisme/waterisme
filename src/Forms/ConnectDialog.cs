using System;
using System.Drawing;
using System.Windows.Forms;
using RemoteDesktop.Core;

namespace RemoteDesktop.Forms;

public sealed class ConnectDialog : Form
{
    public string Nickname { get; private set; } = "";
    public string Ip       { get; private set; } = "";
    public string Pin      { get; private set; } = "";
    public int    Port     => 7890;

    private readonly TextBox _nickBox;
    private readonly TextBox _ipBox;
    private readonly ListBox _historyList;

    // (back, fore, display text)
    private static readonly (Color back, Color fore, string label)[] ColorDefs =
    {
        (Color.Crimson,             Color.White, "紅"),
        (Color.DodgerBlue,          Color.White, "藍"),
        (Color.Gold,                Color.Black, "黃"),
        (Color.FromArgb(20,20,20),  Color.White, "黑"),
        (Color.WhiteSmoke,          Color.Black, "白"),
    };

    public ConnectDialog()
    {
        Text            = "新增連線";
        Size            = new Size(360, 510);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;

        int y = 18;

        // ── Fields ──
        Controls.Add(MakeLabel("暱稱：", 20, y));
        _nickBox = MakeTextBox(80, y, 240); Controls.Add(_nickBox); y += 38;

        Controls.Add(MakeLabel("IP：", 20, y));
        _ipBox = MakeTextBox(80, y, 240); Controls.Add(_ipBox); y += 44;

        // ── History ──
        Controls.Add(new Label
        {
            Text      = "最近連線：",
            Location  = new Point(20, y),
            Size      = new Size(300, 20),
            Font      = new Font("Segoe UI", 9),
            ForeColor = Color.Gray,
        });
        y += 20;

        _historyList = new ListBox
        {
            Location    = new Point(20, y),
            Size        = new Size(300, 76),
            Font        = new Font("Segoe UI", 10),
            BorderStyle = BorderStyle.FixedSingle,
        };
        _historyList.DoubleClick            += HistoryDoubleClick;
        _historyList.SelectedIndexChanged   += HistorySelected;
        Controls.Add(_historyList);
        LoadHistory();
        y += 82;

        // ── Color hint ──
        Controls.Add(new Label
        {
            Text      = "點選被控端螢幕顯示的顏色即可連線：",
            Location  = new Point(20, y),
            Size      = new Size(320, 22),
            Font      = new Font("Segoe UI", 9),
            ForeColor = Color.FromArgb(100, 180, 255),
        });
        y += 28;

        // ── 5 Color buttons ──
        int btnW = 52, btnH = 52, btnGap = 8;
        int totalW = btnW * 5 + btnGap * 4;
        int startX = (340 - totalW) / 2;

        for (int i = 0; i < ColorDefs.Length; i++)
        {
            var (back, fore, label) = ColorDefs[i];
            var btn = new Button
            {
                Location  = new Point(startX + i * (btnW + btnGap), y),
                Size      = new Size(btnW, btnH),
                Font      = new Font("Segoe UI", 15, FontStyle.Bold),
                BackColor = back,
                ForeColor = fore,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand,
                Text      = label,
                Tag       = label,       // store color name
            };
            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
            btn.Click += ColorClick;
            Controls.Add(btn);
        }
        y += btnH + 16;

        // ── OR divider ──
        Controls.Add(new Label
        {
            Text      = "─────────  或  ─────────",
            Location  = new Point(20, y),
            Size      = new Size(320, 18),
            Font      = new Font("Segoe UI", 8),
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleCenter,
        });
        y += 22;

        // ── Custom PIN textbox + connect button ──
        Controls.Add(new Label
        {
            Text      = "輸入 PIN：",
            Location  = new Point(20, y + 4),
            Size      = new Size(68, 22),
            Font      = new Font("Segoe UI", 10),
        });
        var pinBox = new TextBox
        {
            Location  = new Point(90, y),
            Size      = new Size(150, 28),
            Font      = new Font("Segoe UI", 11),
            MaxLength = 32,
        };
        Controls.Add(pinBox);

        var pinConnectBtn = new Button
        {
            Text      = "用 PIN 連線",
            Location  = new Point(248, y - 2),
            Size      = new Size(92, 32),
            Font      = new Font("Segoe UI", 9),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
        };
        pinConnectBtn.Click += (_, _) =>
        {
            string ip = _ipBox.Text.Trim();
            if (ip.Length == 0) { ShowErr("請先輸入 IP 位址。"); return; }
            string p = pinBox.Text.Trim();
            if (p.Length == 0) { ShowErr("請輸入 PIN。"); return; }
            Nickname = _nickBox.Text.Trim().Length > 0 ? _nickBox.Text.Trim() : ip;
            Ip = ip;
            Pin = p;
            ConnectionHistory.AddOrUpdate(Nickname, Ip, Port);
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(pinConnectBtn);
        y += 40;

        // ── Cancel ──
        var cancel = new Button
        {
            Text     = "取消",
            Location = new Point(130, y),
            Size     = new Size(100, 34),
            Font     = new Font("Segoe UI", 10),
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancel);
        CancelButton = cancel;
        AcceptButton = pinConnectBtn;     // Enter → 用 PIN 連線
    }

    // ── Color clicked ────────────────────────────────────────────────────────

    private void ColorClick(object? s, EventArgs e)
    {
        if (s is not Button btn) return;
        string ip = _ipBox.Text.Trim();
        if (ip.Length == 0) { ShowErr("請先輸入 IP 位址。"); return; }
        Nickname = _nickBox.Text.Trim().Length > 0 ? _nickBox.Text.Trim() : ip;
        Ip  = ip;
        Pin = btn.Tag as string ?? btn.Text;
        ConnectionHistory.AddOrUpdate(Nickname, Ip, Port);
        DialogResult = DialogResult.OK;
        Close();
    }

    // ── History ──────────────────────────────────────────────────────────────

    private void HistorySelected(object? s, EventArgs e)
    {
        if (_historyList.SelectedItem is ConnectionEntry entry)
        {
            _nickBox.Text = entry.Nickname;
            _ipBox.Text   = entry.Ip;
        }
    }

    private void HistoryDoubleClick(object? s, EventArgs e)
    {
        if (_historyList.SelectedItem is ConnectionEntry entry)
        {
            _nickBox.Text = entry.Nickname;
            _ipBox.Text   = entry.Ip;
        }
    }

    private void LoadHistory()
    {
        _historyList.Items.Clear();
        foreach (var e in ConnectionHistory.Load())
            _historyList.Items.Add(e);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ShowErr(string msg) =>
        MessageBox.Show(msg, "錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static Label MakeLabel(string text, int x, int y) => new()
    {
        Text     = text,
        Location = new Point(x, y + 4),
        Size     = new Size(58, 22),
        Font     = new Font("Segoe UI", 10),
    };

    private static TextBox MakeTextBox(int x, int y, int w) => new()
    {
        Location = new Point(x, y),
        Size     = new Size(w, 28),
        Font     = new Font("Segoe UI", 11),
    };
}
