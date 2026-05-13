# Remote Desktop — Project Rules

## Version Number
**Every code change must increment the version number.**

Version is defined in one place:
```
src/Core/Protocol.cs  →  internal static class AppInfo { public const string Version = "x.x"; }
```

Increment rules:
- Bug fix or small tweak → bump patch (e.g. 1.2 → 1.3)
- New feature → bump minor (e.g. 1.2 → 1.3, or 1.9 → 2.0 for major milestones)

Current version: **1.2** (as of 2026-05-14)

## Project Structure
```
src/Core/
  Protocol.cs        — message types, Msg helpers, AppInfo.Version, PinColors
  SlaveServer.cs     — multi-master TCP server (thread-per-client)
  MasterClient.cs    — TCP client, exposes SlaveVersion
  DxgiCapture.cs     — GDI+ screen capture (Screen.AllScreens logical pixels)
  InputSimulator.cs  — Win32 mouse/keyboard injection
  ClipboardSync.cs   — bidirectional clipboard sync
  ConnectionHistory.cs

src/Forms/
  MainForm.cs        — connection list, opens Slave/Master windows
  ConnectDialog.cs   — 5 color buttons (紅藍黃黑白), IP + nickname entry
  SlaveForm.cs       — shows large color swatch + IP + status
  MonitorWindow.cs   — remote screen display, toolbar, input forwarding

src/Program.cs
```

## PIN / Auth Flow
- Slave picks a random color (PinColors.Random()) on startup
- Slave displays a large color swatch on SlaveForm
- Master's ConnectDialog shows 5 color buttons immediately (no pre-connection needed)
- User clicks the color matching the slave's swatch → sent as AuthRequest PIN
- Slave verifies color string matches

## Build & Publish
```
cd src
dotnet build -c Debug                          # quick check
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o ..\publish
```
Output: `publish\RemoteDesktop.exe`

> Do NOT use Visual Studio Publish — it fails due to a VS WebTools plugin bug.
