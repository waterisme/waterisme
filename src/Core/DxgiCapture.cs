using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace RemoteDesktop.Core;

public sealed class DxgiCapture : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _screenLeft;
    private readonly int _screenTop;

    public int Width  => _width;
    public int Height => _height;

    // outputIndex = screen index sorted left-to-right (logical pixel coords via Screen.AllScreens)
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

    // Returns null on error. Uses GDI+ CopyFromScreen for .NET 8 compatibility.
    public Bitmap? Capture()
    {
        try
        {
            var bmp = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(_screenLeft, _screenTop, 0, 0,
                             new Size(_width, _height),
                             CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public bool IsAccessLost(Exception ex) => false;

    public void Dispose() { }
}

public record MonitorInfo(int AdapterIndex, int OutputIndex, Rectangle Bounds);

public static class MonitorHelper
{
    // Returns monitors sorted left-to-right using logical pixel bounds (consistent with CopyFromScreen).
    public static List<MonitorInfo> GetAll() =>
        Screen.AllScreens
              .OrderBy(s => s.Bounds.X)
              .Select((s, i) => new MonitorInfo(0, i, s.Bounds))
              .ToList();
}
