using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RemoteDesktop.Core;

// Polls the local clipboard (text AND image) and fires the matching callback
// when the local user copies something new. Call SetRemoteText/SetRemoteImage
// to push content received from the other side; we update our "last seen"
// signature so the round-trip does not echo back.
public sealed class ClipboardSync : IDisposable
{
    private readonly Action<string>? _onText;
    private readonly Action<byte[]>? _onImage;     // PNG bytes
    private string  _lastText      = "";
    private long    _lastImageHash = 0;
    private bool    _running       = false;
    private Thread? _thread;

    public ClipboardSync(Action<string>? onText, Action<byte[]>? onImage = null)
    {
        _onText  = onText;
        _onImage = onImage;
    }

    public void Start()
    {
        _running = true;
        _thread  = new Thread(Poll) { IsBackground = true, Name = "ClipboardPoll" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Dispose() => _running = false;

    // ── Push remote content into the local clipboard ───────────────────────────
    public void SetRemoteText(string text)
    {
        _lastText      = text;
        _lastImageHash = 0;
        RunSTA(() => { try { Clipboard.SetText(text); } catch { } });
    }

    public void SetRemoteImage(byte[] png)
    {
        _lastText = "";
        RunSTA(() =>
        {
            try
            {
                using var ms  = new MemoryStream(png);
                using var img = Image.FromStream(ms);
                using var bmp = new Bitmap(img);            // independent copy
                // Hash the pixels we are about to set; the next poll reads the
                // SAME pixels back (lossless), so the hashes match → no echo.
                _lastImageHash = HashPixels(bmp);
                Clipboard.SetImage(bmp);
            }
            catch { }
        });
    }

    // ── Poll loop (runs on its own STA thread → Clipboard calls are legal) ──────
    private void Poll()
    {
        while (_running)
        {
            try
            {
                if (_onImage != null && Clipboard.ContainsImage())
                {
                    using var img = Clipboard.GetImage();
                    if (img != null)
                    {
                        using var bmp = new Bitmap(img);
                        long hash = HashPixels(bmp);
                        if (hash != _lastImageHash)
                        {
                            _lastImageHash = hash;
                            _lastText      = "";
                            using var ms = new MemoryStream();
                            bmp.Save(ms, ImageFormat.Png);
                            _onImage(ms.ToArray());
                        }
                    }
                }
                else if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(text) && text != _lastText)
                    {
                        _lastText = text;
                        _onText?.Invoke(text);
                    }
                }
            }
            catch { }
            Thread.Sleep(600);
        }
    }

    // FNV-1a over a 32bpp pixel buffer (sampled for speed). Stable across PNG
    // re-encoding because it hashes raw pixels, not file bytes. LockBits with an
    // explicit Format32bppArgb converts on the fly, so we never own/free `src`.
    private static long HashPixels(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int len = Math.Abs(data.Stride) * data.Height;
            var buf = new byte[len];
            Marshal.Copy(data.Scan0, buf, 0, len);

            ulong hash = 14695981039346656037UL;
            const ulong PRIME = 1099511628211UL;
            hash = (hash ^ (ulong)bmp.Width)  * PRIME;
            hash = (hash ^ (ulong)bmp.Height) * PRIME;
            for (int i = 0; i < len; i += 64) { hash ^= buf[i]; hash *= PRIME; }
            for (int i = Math.Max(0, len - 16); i < len; i++) { hash ^= buf[i]; hash *= PRIME; }
            return (long)hash;
        }
        finally { bmp.UnlockBits(data); }
    }

    // SetRemote* may be called from a non-STA network thread → marshal to STA.
    private static void RunSTA(Action a)
    {
        var t = new Thread(() => a()) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(1500);
    }
}
