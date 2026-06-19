using System;
using System.Drawing;
using System.Net;
using System.Windows.Forms;
using RemoteDesktop.Core;

namespace RemoteDesktop.Forms;

public sealed class SlaveForm : Form
{
    private readonly SlaveServer _server;
    private readonly Label       _statusLabel;
    private readonly Label       _connCountLabel;

    public SlaveForm(string colorPin)
    {
        Text            = "被控端 (Slave)";
        Size            = new Size(420, 360);
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
        // Label inside the swatch showing the color name
        var swatchLabel = new Label
        {
            Text      = colorPin,
            ForeColor = swatchFore,
            Font      = new Font("Segoe UI", 26, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock      = DockStyle.Fill,
        };
        swatch.Controls.Add(swatchLabel);

        // ── Connection count ──
        _connCountLabel = new Label
        {
            Text      = "等待連線中…",
            ForeColor = Color.Gray,
            Font      = new Font("Segoe UI", 10),
            TextAlign = ContentAlignment.MiddleCenter,
            Location  = new Point(0, 208),
            Size      = new Size(420, 24),
        };

        // ── Status ──
        _statusLabel = new Label
        {
            Text      = "啟動中…",
            ForeColor = Color.Gray,
            Font      = new Font("Segoe UI", 10),
            TextAlign = ContentAlignment.MiddleCenter,
            Location  = new Point(0, 234),
            Size      = new Size(420, 24),
        };

        // ── Stop button ──
        var stopBtn = new Button
        {
            Text      = "停止",
            Location  = new Point(155, 272),
            Size      = new Size(110, 36),
            Font      = new Font("Segoe UI", 11),
            BackColor = Color.FromArgb(180, 50, 50),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        stopBtn.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
            { title, ipLabel, hint, swatch, _connCountLabel, _statusLabel, stopBtn });

        // ── Start server ──
        _server = new SlaveServer(colorPin);
        _server.StatusChanged      += text => SafeInvoke(() =>
        {
            _statusLabel.Text      = text;
            _statusLabel.ForeColor = Color.Gray;
        });
        _server.ClientConnected    += (count) => SafeInvoke(() =>
        {
            _connCountLabel.Text      = $"● {count} 個主控端連線中";
            _connCountLabel.ForeColor = Color.LimeGreen;
        });
        _server.ClientDisconnected += (count) => SafeInvoke(() =>
        {
            if (count == 0)
            {
                _connCountLabel.Text      = "等待連線中…";
                _connCountLabel.ForeColor = Color.Gray;
            }
            else
            {
                _connCountLabel.Text      = $"● {count} 個主控端連線中";
                _connCountLabel.ForeColor = Color.LimeGreen;
            }
        });
        _server.Start();
    }

    // Map color name → (background, foreground)
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
        _server.Dispose();
        base.OnFormClosing(e);
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
