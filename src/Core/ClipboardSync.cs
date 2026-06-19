using System;
using System.Threading;
using System.Windows.Forms;

namespace RemoteDesktop.Core;

// Polls the clipboard and fires OnChanged when it detects new text from the local user.
// Call SetRemote() to push text received from the other side (suppresses the round-trip echo).
public sealed class ClipboardSync : IDisposable
{
    private readonly Action<string> _onChanged;
    private string  _last     = "";
    private bool    _suppress = false;
    private bool    _running  = false;
    private Thread? _thread;

    public ClipboardSync(Action<string> onChanged) => _onChanged = onChanged;

    public void Start()
    {
        _running = true;
        _thread  = new Thread(Poll) { IsBackground = true, Name = "ClipboardPoll" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void SetRemote(string text)
    {
        _suppress = true;
        _last     = text;
        RunSTA(() => { try { Clipboard.SetText(text); } catch { } });
    }

    public void Dispose() => _running = false;

    private void Poll()
    {
        while (_running)
        {
            try
            {
                string text = GetClipboardText();
                if (!string.IsNullOrEmpty(text) && text != _last)
                {
                    _last = text;
                    if (!_suppress) _onChanged(text);
                    _suppress = false;
                }
            }
            catch { }
            Thread.Sleep(600);
        }
    }

    private static string GetClipboardText()
    {
        string result = "";
        RunSTA(() => { try { if (Clipboard.ContainsText()) result = Clipboard.GetText(); } catch { } });
        return result;
    }

    private static void RunSTA(Action a)
    {
        var t = new Thread(() => a()) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(800);
    }
}
