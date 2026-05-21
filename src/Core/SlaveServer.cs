using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace RemoteDesktop.Core;

public sealed class SlaveServer : IDisposable
{
    public event Action<string>? StatusChanged;
    public event Action<int>?    ClientConnected;      // passes current count
    public event Action<int>?    ClientDisconnected;   // passes remaining count

    private readonly string  _colorPin;        // randomly generated each Slave session
    private readonly string? _customPin;       // user-defined PIN (persistent), nullable
    private readonly int     _port;
    private TcpListener?     _listener;
    private bool             _running;

    // Active client sockets — replaced ConcurrentBag (which never removes) with a Set we explicitly manage
    private readonly HashSet<TcpClient> _activeClients = new();
    private readonly object             _clientsLock   = new();

    private int      _clientCount;            // currently connected
    public  int      TotalAcceptedClients;    // monotonic counter for stats UI
    public  DateTime StartedAt { get; } = DateTime.UtcNow;

    public SlaveServer(string colorPin, string? customPin = null, int port = 7890)
    {
        _colorPin  = colorPin;
        _customPin = string.IsNullOrEmpty(customPin) ? null : customPin;
        _port      = port;
    }

    public void Start()
    {
        _running = true;
        new Thread(ServerLoop) { IsBackground = true, Name = "SlaveServer" }.Start();
    }

    public void Dispose()
    {
        _running = false;
        _listener?.Stop();
        lock (_clientsLock)
        {
            foreach (var c in _activeClients)
                try { c.Close(); } catch { }
            _activeClients.Clear();
        }
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
                Interlocked.Increment(ref TotalAcceptedClients);
                ConfigureKeepAlive(client);
                lock (_clientsLock) _activeClients.Add(client);
                Report($"連線來自 {client.Client.RemoteEndPoint}");
                new Thread(() => HandleClient(client))
                    { IsBackground = true, Name = "SlaveClient" }.Start();
            }
            catch (SocketException) when (!_running) { break; }
            catch (Exception ex) { Report($"錯誤：{ex.Message}"); Thread.Sleep(1000); }
        }
    }

    /// <summary>
    /// Enable OS-level TCP keepalive so dead masters are detected within ~50 s
    /// instead of the 2-hour default.
    /// </summary>
    private static void ConfigureKeepAlive(TcpClient client)
    {
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            // tcp_keepalive_inproc on Windows: { onoff, time-ms, interval-ms }
            var values = new byte[12];
            BitConverter.GetBytes((uint)1).CopyTo(values, 0);        // on
            BitConverter.GetBytes((uint)30000).CopyTo(values, 4);    // first probe after 30 s idle
            BitConverter.GetBytes((uint)5000).CopyTo(values, 8);     // retry every 5 s
            client.Client.IOControl(IOControlCode.KeepAliveValues, values, null);
        }
        catch { }
    }

    private void HandleClient(TcpClient client)
    {
        client.NoDelay = true;
        var stream = client.GetStream();

        try
        {
            // ── Send PIN challenge (master reads + discards; PIN already known via color/manual entry) ──
            Send(stream, Msg.PinChallenge(new[] { _colorPin }));

            // ── Auth ──
            var authMsg = Message.Read(stream);
            if (authMsg == null) return;
            if (authMsg.Type != MessageType.AuthRequest)
            {
                Send(stream, Msg.AuthResponse(false)); return;
            }
            string raw          = System.Text.Encoding.UTF8.GetString(authMsg.Payload);
            string[] parts      = raw.Split('|');
            string suppliedPin  = parts[0];
            string masterVersion = parts.Length > 1 ? parts[1] : "?";

            bool ok = suppliedPin == _colorPin
                   || (_customPin != null && suppliedPin == _customPin);
            if (!ok)
            {
                Send(stream, Msg.AuthResponse(false));
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

            // Per-session pause state
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

            // ── Cursor thread (independent of frame stream so the cursor is
            // smooth even when the screen content is static) ──
            new Thread(() => CursorLoop(monitors, sendQueue))
                { IsBackground = true, Name = "SlaveCursor" }.Start();

            // ── Application-level keepalive: send Ping every 20 s ──
            long lastReceivedMs = Environment.TickCount64;
            var pingTimer = new System.Threading.Timer(_ =>
            {
                try { Enqueue(Msg.Ping()); } catch { }
            }, null, 20000, 20000);

            // ── Input receive loop with read timeout ──
            stream.ReadTimeout = 90000;       // 90 s — triggers IOException → break
            try
            {
                while (_running && client.Connected)
                {
                    var msg = Message.Read(stream);
                    if (msg == null) break;
                    lastReceivedMs = Environment.TickCount64;
                    ProcessInput(msg, clipboard, monitorPaused, Enqueue);
                }
            }
            catch { /* read timeout / socket dead / etc. */ }

            pingTimer.Dispose();
            sendQueue.CompleteAdding();
        }
        finally
        {
            int remaining = Interlocked.Decrement(ref _clientCount);
            // Decrement only if we got past the auth+increment block; clamp at >= 0.
            if (remaining < 0) Interlocked.Exchange(ref _clientCount, 0);
            else ClientDisconnected?.Invoke(remaining);

            lock (_clientsLock) _activeClients.Remove(client);
            try { client.Close(); } catch { }
            Report(remaining > 0 ? $"{remaining} 個主控端仍在連線中…" : "連線斷開，等待下一個連線…");

            // Level-2: drop GDI/bitmap garbage between sessions to keep long-running slave lean.
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    // ── Capture ──────────────────────────────────────────────────────────────

    private static readonly ImageCodecInfo    JpegCodec  = GetJpegCodec();
    private static readonly EncoderParameters JpegParams = MakeJpegParams(80L);

    private const int TILE              = 64;
    private const int KEYFRAME_INTERVAL = 300;   // ~10 s @ 30 fps — force full frame
    private const float MAX_TILE_RATIO  = 0.5f;  // > 50% tiles changed → send full frame instead

    private void CaptureLoop(MonitorInfo monitor, int idx, BlockingCollection<byte[]> queue, bool[] monitorPaused)
    {
        DxgiCapture? cap = null;
        try { cap = new DxgiCapture(monitor.AdapterIndex, monitor.OutputIndex); }
        catch { return; }

        long lastHash         = 0;
        byte[]? prevPixels    = null;    // BGRA pixel buffer of last sent frame
        int     prevW         = 0;
        int     prevH         = 0;
        int     framesSinceKf = KEYFRAME_INTERVAL;   // start at threshold → first frame is keyframe

        while (_running && !queue.IsAddingCompleted)
        {
            if (idx < monitorPaused.Length && monitorPaused[idx])
            {
                Thread.Sleep(50); continue;
            }

            try
            {
                var bmp = cap.Capture();
                if (bmp == null) continue;

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

                long hash = QuickHash(bmp);
                if (hash == lastHash)
                {
                    bmp.Dispose();
                    Thread.Sleep(30);
                    continue;
                }
                lastHash = hash;

                int w = bmp.Width, h = bmp.Height;
                byte[] pixels = ExtractPixels(bmp);                // 32bpp BGRA, length = w*h*4

                bool needsKeyframe = prevPixels == null
                                  || prevW != w || prevH != h
                                  || framesSinceKf >= KEYFRAME_INTERVAL;

                if (!needsKeyframe)
                {
                    var changed = FindChangedTiles(pixels, prevPixels!, w, h);
                    int totalTiles = ((w + TILE - 1) / TILE) * ((h + TILE - 1) / TILE);
                    if (changed.Count == 0)
                    {
                        // No tile changed — hash differed but visual content didn't (e.g. cursor blink in
                        // a sub-pixel region). Skip the send.
                        bmp.Dispose();
                        Thread.Sleep(30);
                        continue;
                    }
                    if ((float)changed.Count / totalTiles > MAX_TILE_RATIO)
                    {
                        // Too much changed — full frame is cheaper.
                        needsKeyframe = true;
                    }
                    else
                    {
                        var tiles = EncodeTiles(bmp, changed);
                        queue.TryAdd(Msg.ScreenTiles(idx, w, h, tiles).ToBytes(), 5);
                        prevPixels = pixels;
                        prevW = w; prevH = h;
                        framesSinceKf++;
                        bmp.Dispose();
                        Thread.Sleep(30);
                        continue;
                    }
                }

                // ── Full / keyframe path ──
                byte[] jpeg;
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, JpegCodec, JpegParams);
                    jpeg = ms.ToArray();
                }
                queue.TryAdd(Msg.ScreenData(idx, jpeg).ToBytes(), 5);
                prevPixels    = pixels;
                prevW         = w;
                prevH         = h;
                framesSinceKf = 0;
                bmp.Dispose();
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

    // ── Tile helpers ─────────────────────────────────────────────────────────

    /// <summary>Copies the bitmap's raw pixels into a fresh byte[] using its
    /// NATIVE pixel format — avoids per-frame GDI+ format conversion which
    /// caused tile-diff comparisons to behave inconsistently.</summary>
    private static byte[] ExtractPixels(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
        try
        {
            int len = data.Stride * data.Height;
            var buf = new byte[len];
            Marshal.Copy(data.Scan0, buf, 0, len);
            return buf;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>
    /// Returns the list of tile rectangles whose pixels differ between
    /// <paramref name="cur"/> and <paramref name="prev"/>.
    /// </summary>
    private static List<Rectangle> FindChangedTiles(byte[] cur, byte[] prev, int w, int h)
    {
        var changed = new List<Rectangle>();
        int stride  = w * 4;
        for (int ty = 0; ty < h; ty += TILE)
        {
            int th = Math.Min(TILE, h - ty);
            for (int tx = 0; tx < w; tx += TILE)
            {
                int tw   = Math.Min(TILE, w - tx);
                bool same = true;
                for (int row = 0; row < th && same; row++)
                {
                    int offset = (ty + row) * stride + tx * 4;
                    int bytes  = tw * 4;
                    var a = cur.AsSpan(offset, bytes);
                    var b = prev.AsSpan(offset, bytes);
                    if (!a.SequenceEqual(b)) same = false;
                }
                if (!same) changed.Add(new Rectangle(tx, ty, tw, th));
            }
        }
        return changed;
    }

    /// <summary>Encodes each tile region as a small JPEG.</summary>
    private static List<Msg.Tile> EncodeTiles(Bitmap source, List<Rectangle> rects)
    {
        var list = new List<Msg.Tile>(rects.Count);
        foreach (var r in rects)
        {
            using var tile = source.Clone(r, source.PixelFormat);
            using var ms   = new MemoryStream();
            tile.Save(ms, JpegCodec, JpegParams);
            list.Add(new Msg.Tile(
                (short)r.X, (short)r.Y, (short)r.Width, (short)r.Height, ms.ToArray()));
        }
        return list;
    }

    private static unsafe long QuickHash(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
        try
        {
            byte* ptr = (byte*)data.Scan0;
            int   len = data.Stride * data.Height;
            ulong hash = 14695981039346656037UL;
            const ulong PRIME = 1099511628211UL;
            for (int i = 0; i < len; i += 256)
            {
                hash ^= ptr[i];
                hash *= PRIME;
            }
            for (int i = len - 8; i < len && i >= 0; i++)
            {
                hash ^= ptr[i];
                hash *= PRIME;
            }
            return (long)hash;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    private void ProcessInput(Message msg, ClipboardSync clipboard, bool[] monitorPaused, Action<Message> enqueue)
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
            case MessageType.Ping:
                enqueue(Msg.Pong()); break;
            case MessageType.Pong:
                /* alive ack — handled by the read-timeout reset above */ break;
        }
    }

    // ── Cursor capture ───────────────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("gdi32.dll")]  private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int    cbSize;
        public int    flags;
        public IntPtr hCursor;
        public POINT  ptScreenPos;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool   fIcon;
        public int    xHotspot;
        public int    yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }
    private const int CURSOR_SHOWING = 0x00000001;

    /// <summary>
    /// Sends CursorUpdate messages ~30 Hz when the cursor moves, changes
    /// shape, or appears / disappears. PNG payload is only included when
    /// the shape (hCursor handle) actually changed.
    /// </summary>
    private void CursorLoop(List<MonitorInfo> monitors, BlockingCollection<byte[]> queue)
    {
        IntPtr lastHandle  = IntPtr.Zero;
        int    lastFx      = int.MinValue;
        int    lastFy      = int.MinValue;
        int    lastMonitor = -1;
        bool   wasVisible  = false;

        while (_running && !queue.IsAddingCompleted)
        {
            try
            {
                CURSORINFO ci = default;
                ci.cbSize = Marshal.SizeOf<CURSORINFO>();

                bool gotInfo = GetCursorInfo(ref ci);
                bool visible = gotInfo && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero;

                if (!visible)
                {
                    if (wasVisible)
                    {
                        var msg = Msg.CursorUpdate(Math.Max(0, lastMonitor), 0, 0,
                            false, 0, 0, Array.Empty<byte>());
                        queue.TryAdd(msg.ToBytes(), 5);
                        wasVisible = false;
                    }
                    Thread.Sleep(33); continue;
                }

                // Find which monitor the cursor sits on
                int monitorIdx = -1;
                Rectangle bounds = Rectangle.Empty;
                for (int i = 0; i < monitors.Count; i++)
                {
                    var b = monitors[i].Bounds;
                    if (ci.ptScreenPos.X >= b.Left && ci.ptScreenPos.X < b.Right &&
                        ci.ptScreenPos.Y >= b.Top  && ci.ptScreenPos.Y < b.Bottom)
                    { monitorIdx = i; bounds = b; break; }
                }
                if (monitorIdx < 0) { Thread.Sleep(33); continue; }

                // Map physical → frame-space coords (matches the scaled bitmap the master receives)
                int frameW = bounds.Width, frameH = bounds.Height;
                if (frameW > 1920 || frameH > 1080)
                {
                    float s = Math.Min(1920f / frameW, 1080f / frameH);
                    frameW = (int)(bounds.Width  * s);
                    frameH = (int)(bounds.Height * s);
                }
                int xInMon = ci.ptScreenPos.X - bounds.Left;
                int yInMon = ci.ptScreenPos.Y - bounds.Top;
                int fx     = xInMon * frameW / bounds.Width;
                int fy     = yInMon * frameH / bounds.Height;

                bool shapeChanged = ci.hCursor != lastHandle;
                bool moved        = fx != lastFx || fy != lastFy || monitorIdx != lastMonitor;
                bool reappeared   = !wasVisible;

                if (!shapeChanged && !moved && !reappeared)
                {
                    Thread.Sleep(33); continue;
                }

                byte[] png  = Array.Empty<byte>();
                int    hotX = 0, hotY = 0;
                if (shapeChanged || reappeared)
                {
                    png = EncodeCursorPng(ci.hCursor, out hotX, out hotY);
                    lastHandle = ci.hCursor;
                }

                var update = Msg.CursorUpdate(monitorIdx, fx, fy, true, hotX, hotY, png);
                queue.TryAdd(update.ToBytes(), 5);

                lastFx      = fx;
                lastFy      = fy;
                lastMonitor = monitorIdx;
                wasVisible  = true;
            }
            catch { /* GDI hiccup — retry next tick */ }

            Thread.Sleep(33);
        }
    }

    private static byte[] EncodeCursorPng(IntPtr hCursor, out int hotspotX, out int hotspotY)
    {
        hotspotX = hotspotY = 0;
        if (hCursor == IntPtr.Zero) return Array.Empty<byte>();
        if (!GetIconInfo(hCursor, out ICONINFO ii)) return Array.Empty<byte>();
        hotspotX = ii.xHotspot;
        hotspotY = ii.yHotspot;
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(hCursor);
            using var bmp  = icon.ToBitmap();
            using var ms   = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return Array.Empty<byte>(); }
        finally
        {
            if (ii.hbmMask  != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
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
