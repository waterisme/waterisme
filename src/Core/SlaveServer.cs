using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RemoteDesktop.Core;

public sealed class SlaveServer : IDisposable
{
    public event Action<string>? StatusChanged;
    public event Action<int>?    ClientConnected;      // passes current count
    public event Action<int>?    ClientDisconnected;   // passes remaining count

    private readonly string  _pin;
    private readonly int     _port;
    private TcpListener?     _listener;
    private bool             _running;

    // All currently active client sockets — for clean shutdown
    private readonly ConcurrentBag<TcpClient> _activeClients = new();
    private int _clientCount = 0;

    public SlaveServer(string pin, int port = 7890) { _pin = pin; _port = port; }

    public void Start()
    {
        _running = true;
        new Thread(ServerLoop) { IsBackground = true, Name = "SlaveServer" }.Start();
    }

    public void Dispose()
    {
        _running = false;
        _listener?.Stop();
        foreach (var c in _activeClients)
            try { c.Close(); } catch { }
    }

    // ── Server loop ──────────────────────────────────────────────────────────

    private void ServerLoop()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Report("等待連線中…");

        while (_running)
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                _activeClients.Add(client);
                Report($"連線來自 {client.Client.RemoteEndPoint}");
                new Thread(() => HandleClient(client))
                    { IsBackground = true, Name = "SlaveClient" }.Start();
            }
            catch (SocketException) when (!_running) { break; }
            catch (Exception ex) { Report($"錯誤：{ex.Message}"); Thread.Sleep(1000); }
        }
    }

    private void HandleClient(TcpClient client)
    {
        client.NoDelay = true;
        var stream = client.GetStream();

        // ── Send PIN challenge (master reads + discards; PIN already known via color UI) ──
        Send(stream, Msg.PinChallenge(new[] { _pin }));

        // ── Auth ──
        var authMsg = Message.Read(stream);
        if (authMsg == null) { client.Close(); return; }  // client disconnected after peeking challenge
        if (authMsg.Type != MessageType.AuthRequest)
        {
            Send(stream, Msg.AuthResponse(false)); client.Close(); return;
        }
        string raw   = System.Text.Encoding.UTF8.GetString(authMsg.Payload);
        string[] parts = raw.Split('|');
        string pin  = parts[0];
        string masterVersion = parts.Length > 1 ? parts[1] : "?";

        if (pin != _pin)
        {
            Send(stream, Msg.AuthResponse(false)); client.Close();
            Report("驗證失敗 (PIN 錯誤)"); return;
        }
        if (masterVersion != AppInfo.Version)
            Report($"版本警告：主控端 v{masterVersion}，被控端 v{AppInfo.Version}");
        Send(stream, Msg.AuthResponse(true));

        // ── Monitor info ──
        var monitors = MonitorHelper.GetAll();
        var ws = new int[monitors.Count]; var hs = new int[monitors.Count];
        for (int i = 0; i < monitors.Count; i++) { ws[i] = monitors[i].Bounds.Width; hs[i] = monitors[i].Bounds.Height; }
        Send(stream, Msg.MonitorInfo(ws, hs));

        // Per-session pause state (not shared between masters)
        bool[] monitorPaused = new bool[monitors.Count];

        int count = Interlocked.Increment(ref _clientCount);
        ClientConnected?.Invoke(count);
        Report($"已驗證，{count} 個主控端串流中…");

        // ── Send queue ──
        var sendQueue = new BlockingCollection<byte[]>(30);
        new Thread(() =>
        {
            foreach (var buf in sendQueue.GetConsumingEnumerable())
                try { stream.Write(buf, 0, buf.Length); } catch { break; }
        }) { IsBackground = true, Name = "SlaveSend" }.Start();

        void Enqueue(Message m) { var b = m.ToBytes(); sendQueue.TryAdd(b, 5); }

        // ── Clipboard ──
        using var clipboard = new ClipboardSync(text => Enqueue(Msg.ClipboardData(text)));
        clipboard.Start();

        // ── Capture threads ──
        for (int i = 0; i < monitors.Count; i++)
        {
            int idx = i;
            new Thread(() => CaptureLoop(monitors[idx], idx, sendQueue, monitorPaused))
                { IsBackground = true, Name = $"Capture[{idx}]" }.Start();
        }

        // ── Input receive loop ──
        try
        {
            while (_running && client.Connected)
            {
                var msg = Message.Read(stream);
                if (msg == null) break;
                ProcessInput(msg, clipboard, monitorPaused);
            }
        }
        catch { }

        sendQueue.CompleteAdding();
        int remaining = Interlocked.Decrement(ref _clientCount);
        ClientDisconnected?.Invoke(remaining);
        Report(remaining > 0 ? $"{remaining} 個主控端仍在連線中…" : "連線斷開，等待下一個連線…");
        client.Close();
    }

    // ── Capture ──────────────────────────────────────────────────────────────

    private static readonly ImageCodecInfo    JpegCodec  = GetJpegCodec();
    private static readonly EncoderParameters JpegParams = MakeJpegParams(80L);

    private void CaptureLoop(MonitorInfo monitor, int idx, BlockingCollection<byte[]> queue, bool[] monitorPaused)
    {
        DxgiCapture? cap = null;
        try { cap = new DxgiCapture(monitor.AdapterIndex, monitor.OutputIndex); }
        catch { return; }

        while (_running && !queue.IsAddingCompleted)
        {
            // Pause when master says so
            if (idx < monitorPaused.Length && monitorPaused[idx])
            {
                Thread.Sleep(50); continue;
            }

            try
            {
                var bmp = cap.Capture();
                if (bmp == null) continue;

                // Scale down if larger than 1920×1080 to reduce bandwidth
                if (bmp.Width > 1920 || bmp.Height > 1080)
                {
                    float scale  = Math.Min(1920f / bmp.Width, 1080f / bmp.Height);
                    int   nw     = (int)(bmp.Width  * scale);
                    int   nh     = (int)(bmp.Height * scale);
                    var   scaled = new Bitmap(nw, nh);
                    using (var g = System.Drawing.Graphics.FromImage(scaled))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(bmp, 0, 0, nw, nh);
                    }
                    bmp.Dispose();
                    bmp = scaled;
                }

                byte[] jpeg;
                using (bmp) using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, JpegCodec, JpegParams);
                    jpeg = ms.ToArray();
                }
                queue.TryAdd(Msg.ScreenData(idx, jpeg).ToBytes(), 5);
                Thread.Sleep(30);
            }
            catch (Exception ex) when (cap.IsAccessLost(ex))
            {
                cap.Dispose(); Thread.Sleep(1500);
                try { cap = new DxgiCapture(monitor.AdapterIndex, monitor.OutputIndex); }
                catch { break; }
            }
            catch { Thread.Sleep(100); }
        }
        cap?.Dispose();
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    private void ProcessInput(Message msg, ClipboardSync clipboard, bool[] monitorPaused)
    {
        switch (msg.Type)
        {
            case MessageType.MouseMove:
                var (mm, mx, my) = Msg.ParseMouseMove(msg.Payload);
                InputSimulator.MoveMouse(mm, mx, my); break;
            case MessageType.MouseClick:
                var (cm, cx, cy, btn, down) = Msg.ParseMouseClick(msg.Payload);
                InputSimulator.ClickMouse(cm, cx, cy, btn, down); break;
            case MessageType.MouseScroll:
                var (sm, sx, sy, delta) = Msg.ParseMouseScroll(msg.Payload);
                InputSimulator.ScrollMouse(sm, sx, sy, delta); break;
            case MessageType.KeyEvent:
                var (vk, kdown) = Msg.ParseKeyEvent(msg.Payload);
                InputSimulator.KeyEvent(vk, kdown); break;
            case MessageType.ClipboardData:
                clipboard.SetRemote(Msg.ParseClipboard(msg.Payload)); break;
            case MessageType.StreamPause:
                int pi = Msg.ParseMonitorIndex(msg.Payload);
                if (pi < monitorPaused.Length) monitorPaused[pi] = true; break;
            case MessageType.StreamResume:
                int ri = Msg.ParseMonitorIndex(msg.Payload);
                if (ri < monitorPaused.Length) monitorPaused[ri] = false; break;
        }
    }

    private static void Send(NetworkStream s, Message m) { var b = m.ToBytes(); s.Write(b, 0, b.Length); }
    private void Report(string text) => StatusChanged?.Invoke(text);

    private static ImageCodecInfo GetJpegCodec()
    {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
            if (c.MimeType == "image/jpeg") return c;
        throw new Exception("JPEG encoder missing");
    }
    private static EncoderParameters MakeJpegParams(long q)
    {
        var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(Encoder.Quality, q);
        return p;
    }
}
