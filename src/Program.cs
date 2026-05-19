using System;
using System.Windows.Forms;
using RemoteDesktop.Forms;

namespace RemoteDesktop;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Per-monitor v2 ensures Screen.Bounds / BitBlt see physical pixels on
        // high-DPI displays — without this the slave only captures ~2/3 of a
        // 2880×1920 screen on a 150% DPI machine.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
