using System;
using System.IO;
using System.Linq;
using System.Text;

namespace RemoteDesktop.Core;

public enum MessageType : byte
{
    AuthRequest   = 0x01,
    AuthResponse  = 0x02,
    ScreenData    = 0x03,
    MouseMove     = 0x04,
    MouseClick    = 0x05,
    MouseScroll   = 0x06,
    KeyEvent      = 0x07,
    ClipboardData = 0x08,
    MonitorInfo   = 0x09,
    Ping          = 0x0A,
    Pong          = 0x0B,
    StreamPause   = 0x0C,
    StreamResume  = 0x0D,
    PinChallenge  = 0x0E,
    ScreenTiles   = 0x0F,   // delta frame: only changed 64×64 tiles
    CursorUpdate  = 0x10,   // cursor position + (optional) shape
}

public sealed class Message
{
    public MessageType Type    { get; init; }
    public byte[]      Payload { get; init; } = Array.Empty<byte>();

    // Wire format: [int32 bodyLen][byte type][payload...]
    public byte[] ToBytes()
    {
        int bodyLen = 1 + Payload.Length;
        var buf = new byte[4 + bodyLen];
        BitConverter.GetBytes(bodyLen).CopyTo(buf, 0);
        buf[4] = (byte)Type;
        Payload.CopyTo(buf, 5);
        return buf;
    }

    public static Message? Read(Stream stream)
    {
        var lenBuf = new byte[4];
        if (!ReadExact(stream, lenBuf, 4)) return null;
        int bodyLen = BitConverter.ToInt32(lenBuf, 0);
        if (bodyLen < 1 || bodyLen > 12_000_000) return null;

        var body = new byte[bodyLen];
        if (!ReadExact(stream, body, bodyLen)) return null;

        return new Message { Type = (MessageType)body[0], Payload = body[1..] };
    }

    private static bool ReadExact(Stream s, byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = s.Read(buf, offset, count - offset);
            if (n == 0) return false;
            offset += n;
        }
        return true;
    }
}

public static class Msg
{
    public static Message AuthRequest(string pin) =>
        new() { Type = MessageType.AuthRequest,
                Payload = Encoding.UTF8.GetBytes($"{pin}|{AppInfo.Version}") };

    public static Message AuthResponse(bool ok) =>
        new() { Type = MessageType.AuthResponse,
                Payload = new[] { ok ? (byte)1 : (byte)0 }
                          .Concat(Encoding.UTF8.GetBytes(AppInfo.Version)).ToArray() };

    public static (bool ok, string slaveVersion) ParseAuthResponse(byte[] p)
    {
        bool   ok  = p[0] == 1;
        string ver = p.Length > 1 ? Encoding.UTF8.GetString(p, 1, p.Length - 1) : "?";
        return (ok, ver);
    }

    public static Message ScreenData(int monitor, byte[] jpeg)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(monitor); w.Write(jpeg.Length); w.Write(jpeg);
        return new() { Type = MessageType.ScreenData, Payload = ms.ToArray() };
    }
    public static (int monitor, byte[] jpeg) ParseScreenData(byte[] p)
    {
        using var ms = new MemoryStream(p); using var r = new BinaryReader(ms);
        int m = r.ReadInt32(); int l = r.ReadInt32(); return (m, r.ReadBytes(l));
    }

    public static Message MonitorInfo(int[] w, int[] h)
    {
        using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
        bw.Write(w.Length);
        for (int i = 0; i < w.Length; i++) { bw.Write(w[i]); bw.Write(h[i]); }
        return new() { Type = MessageType.MonitorInfo, Payload = ms.ToArray() };
    }
    public static (int[] w, int[] h) ParseMonitorInfo(byte[] p)
    {
        using var ms = new MemoryStream(p); using var r = new BinaryReader(ms);
        int n = r.ReadInt32(); var w = new int[n]; var h = new int[n];
        for (int i = 0; i < n; i++) { w[i] = r.ReadInt32(); h[i] = r.ReadInt32(); }
        return (w, h);
    }

    public static Message MouseMove(int monitor, float x, float y)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(monitor); w.Write(x); w.Write(y);
        return new() { Type = MessageType.MouseMove, Payload = ms.ToArray() };
    }
    public static (int monitor, float x, float y) ParseMouseMove(byte[] p)
    {
        using var ms = new MemoryStream(p); using var r = new BinaryReader(ms);
        return (r.ReadInt32(), r.ReadSingle(), r.ReadSingle());
    }

    public static Message MouseClick(int monitor, float x, float y, int btn, bool down)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(monitor); w.Write(x); w.Write(y); w.Write(btn); w.Write(down);
        return new() { Type = MessageType.MouseClick, Payload = ms.ToArray() };
    }
    public static (int monitor, float x, float y, int btn, bool down) ParseMouseClick(byte[] p)
    {
        using var ms = new MemoryStream(p); using var r = new BinaryReader(ms);
        return (r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadInt32(), r.ReadBoolean());
    }

    public static Message MouseScroll(int monitor, float x, float y, int delta)
    {
        using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
        w.Write(monitor); w.Write(x); w.Write(y); w.Write(delta);
        return new() { Type = MessageType.MouseScroll, Payload = ms.ToArray() };
    }
    public static (int monitor, float x, float y, int delta) ParseMouseScroll(byte[] p)
    {
        using var ms = new MemoryStream(p); using var r = new BinaryReader(ms);
        return (r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadInt32());
    }

    public static Message KeyEvent(ushort vk, bool down) =>
        new() { Type = MessageType.KeyEvent,
                Payload = BitConverter.GetBytes(((uint)vk << 16) | (down ? 1u : 0u)) };
    public static (ushort vk, bool down) ParseKeyEvent(byte[] p)
    {
        uint v = BitConverter.ToUInt32(p, 0); return ((ushort)(v >> 16), (v & 1) == 1);
    }

    public static Message ClipboardData(string text) =>
        new() { Type = MessageType.ClipboardData, Payload = Encoding.UTF8.GetBytes(text) };
    public static string ParseClipboard(byte[] p) => Encoding.UTF8.GetString(p);

    public static Message StreamPause(int monitorIndex) =>
        new() { Type = MessageType.StreamPause, Payload = BitConverter.GetBytes(monitorIndex) };
    public static Message StreamResume(int monitorIndex) =>
        new() { Type = MessageType.StreamResume, Payload = BitConverter.GetBytes(monitorIndex) };
    public static int ParseMonitorIndex(byte[] p) => BitConverter.ToInt32(p, 0);

    public static Message PinChallenge(string[] pins) =>
        new() { Type = MessageType.PinChallenge,
                Payload = Encoding.UTF8.GetBytes(string.Join("|", pins)) };
    public static string[] ParsePinChallenge(byte[] p) =>
        Encoding.UTF8.GetString(p).Split('|');

    public static Message Ping() => new() { Type = MessageType.Ping };
    public static Message Pong() => new() { Type = MessageType.Pong };

    // ── Tile delta frame ─────────────────────────────────────────────────────
    public readonly record struct Tile(short X, short Y, short W, short H, byte[] Jpeg);

    public static Message ScreenTiles(int monitor, int frameW, int frameH, IReadOnlyList<Tile> tiles)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);
        w.Write(monitor);
        w.Write(frameW);
        w.Write(frameH);
        w.Write(tiles.Count);
        foreach (var t in tiles)
        {
            w.Write(t.X);  w.Write(t.Y);  w.Write(t.W);  w.Write(t.H);
            w.Write(t.Jpeg.Length);
            w.Write(t.Jpeg);
        }
        return new Message { Type = MessageType.ScreenTiles, Payload = ms.ToArray() };
    }

    public static (int monitor, int frameW, int frameH, Tile[] tiles) ParseScreenTiles(byte[] p)
    {
        using var ms = new MemoryStream(p);
        using var r  = new BinaryReader(ms);
        int monitor = r.ReadInt32();
        int frameW  = r.ReadInt32();
        int frameH  = r.ReadInt32();
        int count   = r.ReadInt32();
        var tiles   = new Tile[count];
        for (int i = 0; i < count; i++)
        {
            short tx = r.ReadInt16();
            short ty = r.ReadInt16();
            short tw = r.ReadInt16();
            short th = r.ReadInt16();
            int   jl = r.ReadInt32();
            byte[] jpeg = r.ReadBytes(jl);
            tiles[i] = new Tile(tx, ty, tw, th, jpeg);
        }
        return (monitor, frameW, frameH, tiles);
    }

    // ── Cursor stream ────────────────────────────────────────────────────────
    // png.Length == 0 means "shape unchanged since the last CursorUpdate
    // on this monitor"; the master reuses the cached shape.

    public static Message CursorUpdate(
        int monitor, int x, int y, bool visible,
        int hotspotX, int hotspotY, byte[] png)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);
        w.Write(monitor);
        w.Write(x);
        w.Write(y);
        w.Write(visible);
        w.Write(hotspotX);
        w.Write(hotspotY);
        w.Write(png.Length);
        if (png.Length > 0) w.Write(png);
        return new Message { Type = MessageType.CursorUpdate, Payload = ms.ToArray() };
    }

    public static (int monitor, int x, int y, bool visible,
                   int hotspotX, int hotspotY, byte[] png) ParseCursorUpdate(byte[] p)
    {
        using var ms = new MemoryStream(p);
        using var r  = new BinaryReader(ms);
        int  monitor  = r.ReadInt32();
        int  x        = r.ReadInt32();
        int  y        = r.ReadInt32();
        bool visible  = r.ReadBoolean();
        int  hotX     = r.ReadInt32();
        int  hotY     = r.ReadInt32();
        int  pngLen   = r.ReadInt32();
        byte[] png    = pngLen > 0 ? r.ReadBytes(pngLen) : Array.Empty<byte>();
        return (monitor, x, y, visible, hotX, hotY, png);
    }
}

internal static class AppInfo
{
    public const string Version = "2.0";
}

// Five PIN colors shared by slave display and master selection
public static class PinColors
{
    public static readonly string[] Names = { "紅", "藍", "黃", "黑", "白" };

    public static string Random() =>
        Names[System.Random.Shared.Next(Names.Length)];
}
