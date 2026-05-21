using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteDesktop.Core;

/// <summary>
/// Per-monitor screen capture using Win32 BitBlt. The cursor sprite is
/// sent separately by SlaveServer's CursorLoop and overlaid by the master
/// (see Msg.CursorUpdate). With PerMonitorV2 DPI awareness (Program.cs)
/// Screen.Bounds returns physical pixels.
/// </summary>
public sealed class DxgiCapture : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int    ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]  private static extern bool   BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int w, int h,
        IntPtr hdcSrc,  int xSrc,  int ySrc,  uint rop);

    private const uint SRCCOPY    = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

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

    /// <summary>Captures the monitor at physical pixel resolution (no cursor).</summary>
    public Bitmap? Capture()
    {
        // Format32bppArgb so the scaled / unscaled paths use the same byte
        // layout — keeps SlaveServer's tile-diff comparison consistent.
        Bitmap?   bmp    = null;
        Graphics? g      = null;
        IntPtr    destDc = IntPtr.Zero;
        IntPtr    srcDc  = IntPtr.Zero;
        try
        {
            bmp    = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
            g      = Graphics.FromImage(bmp);
            destDc = g.GetHdc();
            srcDc  = GetDC(IntPtr.Zero);
            if (srcDc == IntPtr.Zero) { g.ReleaseHdc(destDc); destDc = IntPtr.Zero; bmp.Dispose(); return null; }

            BitBlt(destDc, 0, 0, _width, _height,
                   srcDc,  _screenLeft, _screenTop,
                   SRCCOPY | CAPTUREBLT);
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

    public bool IsAccessLost(Exception ex) => false;

    public void Dispose() { }
}

public record MonitorInfo(int AdapterIndex, int OutputIndex, Rectangle Bounds);

public static class MonitorHelper
{
    public static List<MonitorInfo> GetAll() =>
        Screen.AllScreens
              .OrderBy(s => s.Bounds.X)
              .Select((s, i) => new MonitorInfo(0, i, s.Bounds))
              .ToList();
}
