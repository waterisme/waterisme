using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteDesktop.Core;

/// <summary>
/// Per-monitor screen capture using Win32 BitBlt + manual cursor overlay.
/// With PerMonitorV2 DPI awareness (Program.cs) Screen.Bounds returns physical
/// pixels, so BitBlt captures the entire physical screen regardless of DPI.
/// </summary>
public sealed class DxgiCapture : IDisposable
{
    // ── Win32 ────────────────────────────────────────────────────────────────
    [DllImport("user32.dll")]  private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]  private static extern int    ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]   private static extern bool   BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc,  int xSrc,  int ySrc,  uint rop);

    [DllImport("user32.dll")]  private static extern bool   GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")]  private static extern bool   GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")]  private static extern bool   DrawIconEx(
        IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cx, int cy,
        int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);
    [DllImport("gdi32.dll")]   private static extern bool   DeleteObject(IntPtr hObject);

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

    private const uint SRCCOPY        = 0x00CC0020;
    private const uint CAPTUREBLT     = 0x40000000;
    private const int  CURSOR_SHOWING = 0x00000001;
    private const int  DI_NORMAL      = 0x0003;

    // ── Fields ───────────────────────────────────────────────────────────────
    private readonly int _screenLeft;
    private readonly int _screenTop;
    private readonly int _width;
    private readonly int _height;

    public int Width  => _width;
    public int Height => _height;

    public DxgiCapture(int adapterIndex, int outputIndex)
    {
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.X).ToArray();
        var screen  = outputIndex < screens.Length ? screens[outputIndex] : Screen.PrimaryScreen!;
        var b       = screen.Bounds;
        _screenLeft = b.Left;
        _screenTop  = b.Top;
        _width      = b.Width;
        _height     = b.Height;
    }

    /// <summary>Captures the monitor (physical pixels) and overlays the OS cursor.</summary>
    public Bitmap? Capture()
    {
        Bitmap?   bmp    = null;
        Graphics? g      = null;
        IntPtr    destDc = IntPtr.Zero;
        IntPtr    srcDc  = IntPtr.Zero;
        try
        {
            bmp    = new Bitmap(_width, _height, PixelFormat.Format32bppRgb);
            g      = Graphics.FromImage(bmp);
            destDc = g.GetHdc();
            srcDc  = GetDC(IntPtr.Zero);
            if (srcDc == IntPtr.Zero) { g.ReleaseHdc(destDc); destDc = IntPtr.Zero; bmp.Dispose(); return null; }

            BitBlt(destDc, 0, 0, _width, _height,
                   srcDc,  _screenLeft, _screenTop,
                   SRCCOPY | CAPTUREBLT);

            // ── Cursor overlay: BitBlt does NOT include the cursor sprite ──
            DrawCursor(destDc);

            return bmp;
        }
        catch
        {
            bmp?.Dispose();
            return null;
        }
        finally
        {
            if (destDc != IntPtr.Zero) g?.ReleaseHdc(destDc);
            g?.Dispose();
            if (srcDc != IntPtr.Zero)  ReleaseDC(IntPtr.Zero, srcDc);
        }
    }

    private void DrawCursor(IntPtr destDc)
    {
        CURSORINFO ci = default;
        ci.cbSize = Marshal.SizeOf<CURSORINFO>();
        if (!GetCursorInfo(ref ci)) return;
        if ((ci.flags & CURSOR_SHOWING) == 0) return;
        if (ci.hCursor == IntPtr.Zero) return;

        // Translate screen position → bitmap-local pixels
        int x = ci.ptScreenPos.X - _screenLeft;
        int y = ci.ptScreenPos.Y - _screenTop;

        // Discard cursors outside this monitor (we still might want to draw partial
        // cursors that overlap the edge, so use a generous margin)
        if (x < -64 || y < -64 || x > _width + 64 || y > _height + 64) return;

        ICONINFO ii = default;
        if (!GetIconInfo(ci.hCursor, out ii)) return;
        try
        {
            DrawIconEx(destDc,
                x - ii.xHotspot, y - ii.yHotspot,
                ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            if (ii.hbmMask  != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
    }

    public bool IsAccessLost(Exception ex) => false;

    public void Dispose() { }
}

public record MonitorInfo(int AdapterIndex, int OutputIndex, Rectangle Bounds);

public static class MonitorHelper
{
    /// <summary>Monitors sorted left-to-right using physical pixel bounds (PerMonitorV2).</summary>
    public static List<MonitorInfo> GetAll() =>
        Screen.AllScreens
              .OrderBy(s => s.Bounds.X)
              .Select((s, i) => new MonitorInfo(0, i, s.Bounds))
              .ToList();
}
