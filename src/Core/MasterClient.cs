using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace RemoteDesktop.Core;

public sealed class MasterClient : IDisposable
{
    public event Action<string>?       StatusChanged;
    public event Action<int, Image>?   ScreenUpdated;
    public event Action<int[], int[]>? MonitorInfoReceived;
    public event Action?               Disconnected;
    /// <summary>
    /// (monitorIdx, frameX, frameY, visible, hotspotX, hotspotY, newShape)
    /// — newShape != null only when the cursor shape changed; otherwise reuse cached.
    /// </summary>
    public event Action<int, int, int, bool, int, int, Bitmap?>? CursorUpdated;

    private TcpClient?     _tcp;
    private NetworkStream? _stream;
    private readonly object _sendLock = new();
    private bool           _running;
    private ClipboardSync? _clipboard;

    // Per-monitor canvas — accumulates tile updates between keyframes
    private readonly Dictionary<int, Bitmap> _canvases = new();
    private readonly object _canvasLock = new();

    public bool   IsConnected  => _running && (_tcp?.Connected ?? false);
    public string SlaveVersion { get; private set; } = "";

    public bool Connect(string host, string pin, int port = 7890)
    {
        try
        {
            _tcp = new TcpClient { NoDelay = true };
            _tcp.Connect(host, port);
            _stream = _tcp.GetStream();

            // Read PinChallenge sent by slave (already chosen by user in dialog, discard)
            var challenge = Message.Read(_stream);
            if (challenge?.Type != MessageType.PinChallenge)
            { Report("連線失敗：未收到 PIN 挑戰"); _tcp.Close(); return false; }

            Send(Msg.AuthRequest(pin));
            var resp = Message.Read(_stream);
            if (resp?.Type != MessageType.AuthResponse) { Report("驗證失敗（回應無效）"); _tcp.Close(); return false; }

            var (ok, slaveVer) = Msg.ParseAuthResponse(resp.Payload);
            SlaveVersion = slaveVer;
            if (!ok) { Report("驗證失敗（PIN 錯誤）"); _tcp.Close(); return false; }

            Report("已連線");
            _running = true;
            new Thread(ReceiveLoop) { IsBackground = true, Name = "MasterRecv" }.Start();

            _clipboard = new ClipboardSync(text => { try { Send(Msg.ClipboardData(text)); } catch { } });
            _clipboard.Start();
            return true;
        }
        catch (Exception ex) { Report($"連線失敗：{ex.Message}"); return false; }
    }

    public void Dispose()
    {
        _running = false;
        _clipboard?.Dispose();
        _tcp?.Close();
        lock (_canvasLock)
        {
            foreach (var b in _canvases.Values) try { b.Dispose(); } catch { }
            _canvases.Clear();
        }
    }

    // ── Receive ───────────────────────────────────────────────────────────────

    private void ReceiveLoop()
    {
        while (_running)
        {
            try
            {
                var msg = Message.Read(_stream!);
                if (msg == null) break;
                switch (msg.Type)
                {
                    case MessageType.ScreenData:
                        HandleScreenData(msg.Payload);
                        break;
                    case MessageType.ScreenTiles:
                        HandleScreenTiles(msg.Payload);
                        break;
                    case MessageType.CursorUpdate:
                        HandleCursorUpdate(msg.Payload);
                        break;
                    case MessageType.MonitorInfo:
                        var (ws, hs) = Msg.ParseMonitorInfo(msg.Payload);
                        MonitorInfoReceived?.Invoke(ws, hs);
                        break;
                    case MessageType.ClipboardData:
                        _clipboard?.SetRemote(Msg.ParseClipboard(msg.Payload));
                        break;
                    case MessageType.Ping:
                        TrySend(Msg.Pong());
                        break;
                }
            }
            catch { break; }
        }
        _clipboard?.Dispose();
        _running = false;
        Disconnected?.Invoke();
    }

    private void HandleScreenData(byte[] payload)
    {
        var (idx, jpeg) = Msg.ParseScreenData(payload);

        // CRITICAL: `new Bitmap(stream)` keeps a reference to the underlying
        // stream; if we dispose the MemoryStream the Bitmap becomes invalid.
        // The fix is `new Bitmap(decoded)` which makes an independent copy.
        Bitmap full;
        using (var ms       = new MemoryStream(jpeg))
        using (var decoded  = Image.FromStream(ms))
            full = new Bitmap(decoded);

        // Store as the per-monitor canvas; subsequent ScreenTiles patch this.
        lock (_canvasLock)
        {
            if (_canvases.TryGetValue(idx, out var old)) old.Dispose();
            _canvases[idx] = full;
        }
        // Hand a private clone to the UI — MonitorWindow owns and disposes it.
        ScreenUpdated?.Invoke(idx, (Image)full.Clone());
    }

    private void HandleCursorUpdate(byte[] payload)
    {
        var (mon, x, y, visible, hotX, hotY, png) = Msg.ParseCursorUpdate(payload);

        Bitmap? newShape = null;
        if (png.Length > 0)
        {
            try
            {
                using var ms  = new MemoryStream(png);
                using var tmp = Image.FromStream(ms);
                newShape = new Bitmap(tmp);                 // independent copy
            }
            catch { newShape = null; }
        }
        CursorUpdated?.Invoke(mon, x, y, visible, hotX, hotY, newShape);
    }

    private void HandleScreenTiles(byte[] payload)
    {
        var (idx, frameW, frameH, tiles) = Msg.ParseScreenTiles(payload);
        Bitmap? snapshot;

        lock (_canvasLock)
        {
            if (!_canvases.TryGetValue(idx, out var canvas)
                || canvas.Width  != frameW
                || canvas.Height != frameH)
            {
                // No baseline yet (or resolution changed) — ignore; we'll get
                // a keyframe within 10 s and pick up from there.
                return;
            }

            using (var g = Graphics.FromImage(canvas))
            {
                foreach (var t in tiles)
                {
                    using var ms      = new MemoryStream(t.Jpeg);
                    using var tileImg = Image.FromStream(ms);
                    g.DrawImage(tileImg, t.X, t.Y, t.W, t.H);
                }
            }
            snapshot = (Bitmap)canvas.Clone();
        }

        ScreenUpdated?.Invoke(idx, snapshot);
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    public void SendMouseMove(int m, float x, float y)                      => TrySend(Msg.MouseMove(m, x, y));
    public void SendMouseClick(int m, float x, float y, int btn, bool down) => TrySend(Msg.MouseClick(m, x, y, btn, down));
    public void SendMouseScroll(int m, float x, float y, int delta)         => TrySend(Msg.MouseScroll(m, x, y, delta));
    public void SendKeyEvent(ushort vk, bool down)                          => TrySend(Msg.KeyEvent(vk, down));
    public void SendStreamPause(int monitorIndex)                           => TrySend(Msg.StreamPause(monitorIndex));
    public void SendStreamResume(int monitorIndex)                          => TrySend(Msg.StreamResume(monitorIndex));

    private void TrySend(Message msg) { try { Send(msg); } catch { } }
    private void Send(Message msg)
    {
        if (_stream == null) return;
        var buf = msg.ToBytes();
        lock (_sendLock) { _stream.Write(buf, 0, buf.Length); }
    }
    private void Report(string text) => StatusChanged?.Invoke(text);
}
