# Session Handover — RemoteDesktop v1.9

> 給接手的 Claude 看：先讀這份，再讀 `CLAUDE.md` + `CHANGELOG.md`。

---

## 目前狀態（2026-05-21）

- **版本**：1.9（master 分支上）
- **Build**：clean，0 警告 0 錯誤
- **Working tree**：乾淨（除非剛產生 release zip）
- **GitHub**：https://github.com/waterisme/waterisme，分支 `master` 含 v1.9 全部變更
- **Tag**：`v1.3-stable`、`v1.8-stable`、`v1.9-stable`
- **Release**：`publish\RemoteDesktop.exe`（如需 zip 用 `打包 + zip`）

## Repo 結構

```
src/
  Core/
    Protocol.cs       — Wire protocol：[int32 len][byte type][payload]；AppInfo.Version、PinColors、所有 Msg helpers
    SlaveServer.cs    — Slave TCP 主邏輯：accept → auth → MonitorInfo → CaptureLoop + CursorLoop + receive
    MasterClient.cs   — Master TCP client：connect/auth、ReceiveLoop、canvas state for tile updates
    DxgiCapture.cs    — Win32 BitBlt 擷取（PerMonitorV2 物理像素）
    InputSimulator.cs — Win32 mouse/keyboard injection
    ClipboardSync.cs  — 雙向剪貼簿同步
    ConnectionHistory.cs
    SlaveConfig.cs    — 自訂 PIN 持久化（%APPDATA%\RemoteDesktop\slave.json）
  Forms/
    MainForm.cs       — 連線管理器
    ConnectDialog.cs  — 5 色按鈕 + 自訂 PIN 輸入
    SlaveForm.cs      — Slave 主視窗（色塊 + PIN 設定 + 統計 + admin 警告）
    MonitorWindow.cs  — 遠端螢幕視窗（自訂 paint + cursor overlay + FPS 顯示）
  Program.cs          — Application.SetHighDpiMode(PerMonitorV2)
  RemoteDesktop.csproj — net8.0-windows、AllowUnsafeBlocks（給 FNV hash 用）

CLAUDE.md            — 專案規則（版本號規定、工作流程規則）
CHANGELOG.md         — v1.0 ~ v1.8 全變更
SESSION-HANDOVER.md  — 這份檔案
README.md            — 一般說明
RemoteDesktop.sln    — VS Solution

publish/             — Release exe 輸出
RemoteDesktop-v1.8.zip — 包裝好的 release
```

## 工作流程規則（不可違反）

1. **改 code 前必須先跟使用者確認**，不可未經授權直接動工
2. **每次改 code，`src/Core/Protocol.cs` 的 `AppInfo.Version` 必須遞增**（patch +0.1）
3. CHANGELOG.md 補上對應條目
4. CLAUDE.md 的 `Current version` 一起更新
5. Build + publish + commit + push 一次完成

## 架構重點（影響後續決策）

### Protocol（自訂 binary）
- Frame：`[int32 bodyLen][byte type][payload bytes]`
- 訊息類型 0x01–0x10 詳見 `Protocol.cs MessageType` enum
- 多 master 同時連線：每個 client 自己一條 thread + per-session 狀態（`monitorPaused[]`, `prevPixels` 等）

### Screen capture 為什麼用 BitBlt 不用 DXGI
- 我們**試過 SharpDX DXGI**：在 .NET 8 不相容，失敗
- 改用 GDI+ `CopyFromScreen`：DPI 邏輯像素問題，只能擷取 2/3 畫面
- 最終 Win32 `BitBlt` + `Application.SetHighDpiMode(PerMonitorV2)`：擷取物理像素，沒問題

### Cursor 為什麼獨立串流
- 試過：BitBlt 後用 `DrawIconEx` 把游標畫進 bitmap → 整個 capture loop freeze
- 改為：獨立 `CursorLoop` thread 30 Hz 送 `CursorUpdate`，master 在 `OnCanvasPaint` 疊上去
- 好處：靜態畫面下游標仍順、不會干擾 tile diff、不會 freeze

### Tile diff（v1.7-v1.8）
- 64×64 tile，`Span<byte>.SequenceEqual` 比對 raw pixels
- 變動 tile > 50% 或每 300 幀（~10s）強制送 keyframe（full JPEG）
- Pixel format 必須統一為 `Format32bppArgb`（v1.8 修正）
- Master canvas：`Dictionary<int, Bitmap>` per monitor，`Graphics.DrawImage` blit tiles
- **重要 trap**：`new Bitmap(MemoryStream)` 會持有 stream 引用，dispose stream 後 Bitmap 失效。要用 `new Bitmap(Image.FromStream(ms))` 做獨立 copy

### PIN 系統
- Slave 啟動隨機選 5 色之一（紅藍黃黑白），UI 顯示色塊
- 使用者可選設「自訂 PIN」（4-32 字元，持久化）
- Slave 同時接受兩種登入：色塊名稱 或 自訂 PIN
- Master 在 ConnectDialog 用 5 顆顏色鈕 或 PIN textbox 任一連線

### 健全性（v1.6 加的）
- TCP keepalive（OS 層）：30s probe，5s 重試
- 應用層 Ping/Pong：每 20s slave 主動送 Ping，master 回 Pong
- Slave socket `ReadTimeout = 90s`
- 連線結束 `GC.Collect()` 釋放 GDI/Bitmap 資源
- 多 master 用 `HashSet<TcpClient> + lock`（不是 ConcurrentBag）

## Build & 發佈指令

```powershell
cd "P:\BE\01.其他留存資料\00.AI資料\coding\waterisme-claude-remote-desktop-software-1CfWb\src"

# Debug build
dotnet build -c Debug

# Release single-file exe（用這個給使用者測試）
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=none `
  -o ..\publish
```

> ⚠ **不要用 Visual Studio Publish**：VS WebTools 在這個專案上會失敗，原因不明。
> 一定要用 `dotnet publish` CLI。

## 未完事項 / 後續可能要做

- **H.264 編碼**（給非 LAN 用）：分支原本叫 `experiment-h264` 就是準備做這個，
  但 v1.4-v1.8 只做了 hash skip + tile diff。Surface Pro 3 有 QuickSync H.264
  硬編能力，但要用 DXGI Output Duplication 才能發揮效益（我們現在用 BitBlt 進 CPU 記憶體）。
- （v1.9 已完成）連線歷史顯示最後使用 PIN
- （v1.9 已完成）Slave 自訂 PIN「清除」按鈕

## 怎麼讓新的 Claude 接手

開新對話視窗時，**第一句話**：

> 請讀以下檔案，整理當前狀態給我：
> - `P:\BE\01.其他留存資料\00.AI資料\coding\waterisme-claude-remote-desktop-software-1CfWb\CLAUDE.md`
> - `...\CHANGELOG.md`
> - `...\SESSION-HANDOVER.md`

新的 Claude 就能無縫接手。如果它說「找不到」，告訴它資料夾在哪。

## 給接手 Claude 的快速心智模型

- 這是 C# .NET 8 WinForms 遠端桌面 app
- Master/Slave 都是同一支 exe，不同模式
- 已經做過 9 個版本（v1.0–v1.8），bug fixes 很多，**改 code 前一定要確認** 並 bump 版本號
- 主要 paint point：DPI、Bitmap 生命週期、GDI handle、執行緒安全
- 用者習慣中文回覆，但程式碼註解我會混用中英
