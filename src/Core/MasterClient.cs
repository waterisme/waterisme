using System;
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

    private TcpClient?     _tcp;
    private NetworkStream? _stream;
    private readonly object _sendLock = new();
    private bool           _running;
    private ClipboardSync? _clipboard;

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
                        var (idx, jpeg) = Msg.ParseScreenData(msg.Payload);
                        using (var ms = new MemoryStream(jpeg))
                        {
                            var img = (Image)Image.FromStream(ms).Clone();
                            ScreenUpdated?.Invoke(idx, img);
                        }
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
