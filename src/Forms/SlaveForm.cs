using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Security.Principal;
using System.Windows.Forms;
using RemoteDesktop.Core;

namespace RemoteDesktop.Forms;

public sealed class SlaveForm : Form
{
    private readonly SlaveServer  _server;
    private readonly Label        _statusLabel;
    private readonly Label        _connCountLabel;
    private readonly Label        _statsLabel;
    private readonly Label        _customPinLabel;
    private readonly SlaveConfig  _config;
    private readonly System.Windows.Forms.Timer _statsTimer;

    public SlaveForm(string colorPin)
    {
        _config = SlaveConfig.Load();

        bool isAdmin = IsRunningAsAdmin();

        Text            = "被控端 (Slave)";
        Size            = new Size(420, isAdmin ? 430 : 500);
        StartPosition   = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        BackColor       = Color.FromArgb(30, 30, 30);

        // ── Title ──
        var title = new Label
        {
            Text      = "被控端模式",
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 16, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Location  = new Point(0, 14),
            Size      = new Size(420, 36),
        };

        // ── IP ──
        var ipLabel = new Label
        {
            Text      = $"本機 IP：{GetLocalIP()}",
            ForeColor = Color.LightGray,
            Font      = new Font("Segoe UI", 12),
            TextAlign = ContentAlignment.MiddleCenter,
            Location  = new Point(0, 58),
            Size      = new Size(420, 28),
        };

        // ── Color swatch ──
        var hint = new Label
        {
            Text      = "請在主控端點選此顏色：",
            ForeColor = Color.LightGray,
            Font      = new Font("Segoe UI", 10),
            TextAlign = ContentAlignment.MiddleCenter,
            Location  = new Point(0, 96),
            Size      = new Size(420, 22),
        };

        var (swatchBack, swatchFore) = ColorForName(colorPin);
        var swatch = new Panel
        {
            BackColor = swatchBack,
            Location  = new Point(130, 124),
            Size      = new Size(160, 72),
        };
        var swatchLabel = new Label
        {
            Text      = colorPin,
            ForeColor = swatchFore,
            Font      = new Font("Segoe UI", 26, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock      = DockStyle.Fill,
        };
        swatch.Controls.Add(swatchLabel);

        // ── Custom PIN row ──
        _customPinLabel = new Label
        {
            Text      = FormatCustomPinLabel(),
            ForeColor = string.IsNullOrEmpty(_config.CustomPin) ? Color.Gray : Color.FromArgb(100, 180, 255),
            Font      = new Font("Segoe UI", 10),
            TextAlign = ContentAlignment.MiddleLeft,
            Location  = new Point(20, 208),
            Size      = new Size(280, 24),
        };
        var pinBtn = new Button
        {
            Text      = "設定 PIN",
            Location  = new Point(310, 206),
            Size      = new Size(90, 28),
            Font      = new Font("Segoe UI", 9),
            BackColor = Color.FromArgb(60, 60, 65),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor    = Cursors.Hand,
        };
        pinBtn.Click += OnSetCustomPin;

        // ── Connection count ──
        _connCountLabel = new Label
        {
            Text      = "等待連線中…",
            ForeColor = Color.Gray,
            Font      = new Font("Segoe UI", 10),
            TextAlign = ContentAlignment.MiddleCenter,
            Location  = new Point(0, 240),
            Size      = new Size(420, 22),
        };

        // ── Stats (uptime / total accepted) ──
        _statsLabel = new Label
        {
            Text      = "",
            ForeColor = Color.DimGray,
            Font      = new Font("Segoe UI", 8),
            TextAlign = ContentAlignment.MiddleCenter,
            Location  = new Point(0, 262),
            Size      = new Size(420, 18),
        };

        // ── Status ──
        _statusLabel = new Label
        {
            Text      = "啟動中…",
            ForeColor = Color.Gray,
            Font      = new Font("Segoe UI", 9),
            TextAlign = ContentAlignment.MiddleCenter,
            Location  = new Point(0, 282),
            Size      = new Size(420, 22),
        };

        // ── Admin warning + elevate button (only when not running as admin) ──
        Label?  adminWarn   = null;
        Label?  adminSub    = null;
        Button? elevateBtn  = null;
        int     adminBlockH = 0;
        if (!isAdmin)
        {
            adminWarn = new Label
            {
                Text      = "⚠ 未以系統管理員身分執行",
                ForeColor = Color.Gold,
                Font      = new Font("Segoe UI", 10, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Location  = new Point(0, 314),
                Size      = new Size(420, 22),
            };
            adminSub = new Label
            {
                Text      = "工作管理員等高權限程式將無法操作",
                ForeColor = Color.LightGray,
                Font      = new Font("Segoe UI", 8),
                TextAlign = ContentAlignment.MiddleCenter,
                Location  = new Point(0, 336),
                Size      = new Size(420, 18),
            };
            elevateBtn = new Button
            {
                Text      = "以系統管理員身分重啟",
                Location  = new Point(120, 358),
                Size      = new Size(180, 32),
                Font      = new Font("Segoe UI", 10),
                BackColor = Color.FromArgb(200, 140, 0),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand,
            };
            elevateBtn.Click += RestartAsAdmin;
            adminBlockH = 86;
        }

        // ── Stop button ──
        int stopBtnY = 314 + adminBlockH;
        var stopBtn = new Button
        {
            Text      = "停止",
            Location  = new Point(155, stopBtnY),
            Size      = new Size(110, 36),
            Font      = new Font("Segoe UI", 11),
            BackColor = Color.FromArgb(180, 50, 50),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        stopBtn.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
            { title, ipLabel, hint, swatch, _customPinLabel, pinBtn,
              _connCountLabel, _statsLabel, _statusLabel, stopBtn });
        if (adminWarn  != null) Controls.Add(adminWarn);
        if (adminSub   != null) Controls.Add(adminSub);
        if (elevateBtn != null) Controls.Add(elevateBtn);

        // ── Start server with BOTH colour PIN and (optional) custom PIN ──
        _server = new SlaveServer(colorPin, _config.CustomPin);
        _server.StatusChanged      += text => SafeInvoke(() =>
        {
            _statusLabel.Text      = text;
            _statusLabel.ForeColor = Color.Gray;
        });
        _server.ClientConnected    += (count) => SafeInvoke(() => UpdateConnCount(count));
        _server.ClientDisconnected += (count) => SafeInvoke(() => UpdateConnCount(count));
        _server.Start();

        // ── Stats refresher ──
        _statsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();
        RefreshStats();
    }

    // ── Custom PIN dialog ────────────────────────────────────────────────────
    private void OnSetCustomPin(object? sender, EventArgs e)
    {
        using var dlg = new CustomPinDialog(_config.CustomPin ?? "");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _config.CustomPin = string.IsNullOrEmpty(dlg.Pin) ? null : dlg.Pin;
        _config.Save();
        _customPinLabel.Text      = FormatCustomPinLabel();
        _customPinLabel.ForeColor = string.IsNullOrEmpty(_config.CustomPin) ? Color.Gray : Color.FromArgb(100, 180, 255);

        MessageBox.Show("PIN 已更新。新設定將在下次啟動 Slave 後生效。",
            "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private string FormatCustomPinLabel() =>
        string.IsNullOrEmpty(_config.CustomPin)
            ? "自訂 PIN：（未設定，僅用顏色登入）"
            : $"自訂 PIN：●●●（已設定）";

    // ── UI helpers ───────────────────────────────────────────────────────────
    private void UpdateConnCount(int count)
    {
        if (count <= 0)
        {
            _connCountLabel.Text      = "等待連線中…";
            _connCountLabel.ForeColor = Color.Gray;
        }
        else
        {
            _connCountLabel.Text      = $"● {count} 個主控端連線中";
            _connCountLabel.ForeColor = Color.LimeGreen;
        }
    }

    private void RefreshStats()
    {
        if (IsDisposed) return;
        var up = DateTime.UtcNow - _server.StartedAt;
        string hms = $"{(int)up.TotalHours:00}:{up.Minutes:00}:{up.Seconds:00}";
        _statsLabel.Text =
            $"運行 {hms}　累計連線 {_server.TotalAcceptedClients} 次";
    }

    private static (Color back, Color fore) ColorForName(string name) => name switch
    {
        "紅" => (Color.Crimson,      Color.White),
        "藍" => (Color.DodgerBlue,   Color.White),
        "黃" => (Color.Gold,         Color.Black),
        "黑" => (Color.FromArgb(20, 20, 20), Color.White),
        "白" => (Color.WhiteSmoke,   Color.Black),
        _    => (Color.Gray,         Color.White),
    };

    private void SafeInvoke(Action a)
    {
        if (IsDisposed) return;
        try { Invoke(a); } catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _statsTimer.Stop();
        _server.Dispose();
        base.OnFormClosing(e);
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private void RestartAsAdmin(object? sender, EventArgs e)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName        = Environment.ProcessPath ?? Application.ExecutablePath,
                UseShellExecute = true,
                Verb            = "runas",
            };
            Process.Start(psi);
            Application.Exit();
        }
        catch (Win32Exception)
        {
            // User cancelled UAC — stay on current session
        }
    }

    private static string GetLocalIP()
    {
        try
        {
            foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return ip.ToString();
        }
        catch { }
        return "未知";
    }
}

// ── Custom PIN dialog ─────────────────────────────────────────────────────────
internal sealed class CustomPinDialog : Form
{
    public string Pin { get; private set; } = "";

    public CustomPinDialog(string existing)
    {
        Text            = "設定自訂 PIN";
        Size            = new Size(380, 230);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false; MinimizeBox = false;
        BackColor       = Color.FromArgb(30, 30, 30);

        Controls.Add(new Label
        {
            Text      = "輸入您要使用的 PIN（任意 4-32 字元）：",
            ForeColor = Color.LightGray,
            Font      = new Font("Segoe UI", 10),
            Location  = new Point(20, 20),
            Size      = new Size(330, 22),
        });
        Controls.Add(new Label
        {
            Text      = "留空可清除已設定的 PIN。",
            ForeColor = Color.DimGray,
            Font      = new Font("Segoe UI", 8),
            Location  = new Point(20, 44),
            Size      = new Size(330, 18),
        });

        var box = new TextBox
        {
            Location  = new Point(20, 70),
            Size      = new Size(330, 30),
            Font      = new Font("Segoe UI", 14),
            MaxLength = 32,
            UseSystemPasswordChar = false,
            Text      = existing,
        };
        Controls.Add(box);

        var ok = new Button
        {
            Text     = "儲存",
            Location = new Point(80, 130),
            Size     = new Size(100, 34),
            Font     = new Font("Segoe UI", 10),
            DialogResult = DialogResult.None,
        };
        ok.Click += (_, _) =>
        {
            string p = box.Text.Trim();
            if (p.Length > 0 && p.Length < 4)
            {
                MessageBox.Show("PIN 至少 4 個字元（或留空清除）。", "錯誤",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Pin = p;
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(ok);

        var cancel = new Button
        {
            Text     = "取消",
            Location = new Point(200, 130),
            Size     = new Size(100, 34),
            Font     = new Font("Segoe UI", 10),
            DialogResult = DialogResult.Cancel,
        };
        Controls.Add(cancel);
        CancelButton = cancel;
        AcceptButton = ok;
    }
}
