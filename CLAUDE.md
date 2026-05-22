# Remote Desktop — Project Rules

> **首次接手請依序讀：本檔案 → `CHANGELOG.md` → `SESSION-HANDOVER.md`**

## 當前版本

**v2.0** (master 分支，2026-05-22)

最近的 stable tag：`v2.0-stable`、`v1.9-stable`、`v1.8-stable`、`v1.3-stable`

## 使用者指令（精簡關鍵字）

當使用者輸入這些精簡指令時，按指定流程執行：

### `打包` — Session packaging（換對話前用）
預設執行以下全部：
1. 檢查 working tree，若有未 commit 變更先 commit 並 push
2. 確認 `AppInfo.Version` 自上一個 tag 後有增加（沒有就警告）
3. Tag 當前 commit 為 `v<當前版本>-stable`，push tag 到 origin
4. 更新 `SESSION-HANDOVER.md` 的「目前狀態」區塊（日期、版本、最近 commit）
5. 更新 `CLAUDE.md` 的「當前版本」
6. 印出總結 + 「換新對話時貼這段話」的 prompt

可加參數：
- `打包 + zip` — 額外用 `dotnet publish` 並產出 `RemoteDesktop-v<版本>.zip`
- `打包 + merge` — 若不在 master，先 merge 回 master 再 tag

### `接手` — 新對話開頭用
依序讀 `CLAUDE.md` → `SESSION-HANDOVER.md` → `CHANGELOG.md`，
回報當前版本、最近做了什麼、未完事項，**不要主動改 code**，
等使用者下指令。

### `返回 v<X.Y>` — 回到某 stable tag
執行 `git checkout v<X.Y>-stable`（detached HEAD 模式檢視），
或 `git reset --hard v<X.Y>-stable` 若使用者要丟掉之後改動。
**操作前先確認**。

## 工作流程規則（必守，不可違反）

1. **改 code 前必須先跟使用者確認**，不可未經授權直接動工
2. **每次改 code，`src/Core/Protocol.cs` 的 `AppInfo.Version` 必須遞增**
   - Bug fix / 小改 → patch +0.1（例如 1.8 → 1.9）
   - 新功能 → minor +0.1（例如 1.9 → 2.0 視重要程度）
3. **`CHANGELOG.md`** 必須補上對應條目
4. **`CLAUDE.md`** 的「當前版本」一起更新
5. **流程**：改 code → build → publish → commit → push 一次完成

## 版本號定義位置（唯一）

```
src/Core/Protocol.cs  →  internal static class AppInfo { public const string Version = "x.x"; }
```

## Project Structure

```
src/Core/
  Protocol.cs        — Wire protocol、AppInfo.Version、PinColors、Msg helpers
  SlaveServer.cs     — Multi-master TCP server（thread-per-client）
  MasterClient.cs    — TCP client，maintains per-monitor canvas
  DxgiCapture.cs     — Win32 BitBlt screen capture（PerMonitorV2 物理像素）
  InputSimulator.cs  — Win32 mouse/keyboard injection
  ClipboardSync.cs   — 雙向剪貼簿
  ConnectionHistory.cs
  SlaveConfig.cs     — 自訂 PIN 持久化（%APPDATA%\RemoteDesktop\slave.json）

src/Forms/
  MainForm.cs        — 連線管理器（Add/Disconnect/Show、Slave 啟動）
  ConnectDialog.cs   — 5 色按鈕 + PIN textbox
  SlaveForm.cs       — Slave UI（色塊 + PIN 設定 + admin 警告 + 統計）
  MonitorWindow.cs   — 遠端螢幕視窗（自訂 paint + 游標 overlay + FPS）

src/Program.cs       — Application.SetHighDpiMode(PerMonitorV2)
```

## PIN / Auth Flow

- Slave 啟動隨機選色（紅/藍/黃/黑/白）→ UI 顯示色塊
- Slave 可選設「自訂 PIN」（4-32 字元，持久化到 AppData）
- Slave 同時接受**色塊名稱** 或 **自訂 PIN** 兩種登入
- Master ConnectDialog 用 5 顆顏色鈕 或 PIN textbox 任一連線

## Protocol（自訂 binary）

Frame: `[int32 bodyLen][byte type][payload bytes]`

Message types（`MessageType` enum in Protocol.cs）：
- 0x01 AuthRequest / 0x02 AuthResponse
- 0x03 ScreenData（完整 JPEG，keyframe）
- 0x04-0x07 Mouse/KeyEvent / 0x08 ClipboardData
- 0x09 MonitorInfo
- 0x0A Ping / 0x0B Pong（每 20s）
- 0x0C StreamPause / 0x0D StreamResume
- 0x0E PinChallenge（slave 連線初送）
- 0x0F ScreenTiles（delta 64×64 tiles）
- 0x10 CursorUpdate（位置 + optional PNG shape）

## Build & Publish

```powershell
cd src
dotnet build -c Debug                          # 快速驗證
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:DebugType=none -o ..\publish
```

輸出：`publish\RemoteDesktop.exe`

> ⚠ **不要用 Visual Studio Publish** — VS WebTools 在此專案會失敗。

## 環境

- .NET 8 WinForms (`net8.0-windows`)
- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` — 給 FNV hash 用
- 路徑：`P:\BE\01.其他留存資料\00.AI資料\coding\waterisme-claude-remote-desktop-software-1CfWb`
- GitHub：https://github.com/waterisme/waterisme（branch: `master`）

## 重要設計決策（影響後續工作）

- **screen capture 用 Win32 BitBlt**（不用 DXGI／SharpDX 在 .NET 8 壞掉、不用 CopyFromScreen DPI 有問題）
- **`Application.SetHighDpiMode(PerMonitorV2)`** 才能讓 `Screen.Bounds` 回傳物理像素
- **Pixel format 必須統一為 `Format32bppArgb`**（避免 tile diff 比對不一致）
- **`new Bitmap(MemoryStream)` 是地雷**：要用 `new Bitmap(Image.FromStream(ms))` 做獨立 copy
- **多 master 用 `HashSet<TcpClient> + lock`**（不用 ConcurrentBag — 無法移除）
- **Cursor 獨立串流**（不要畫進 capture bitmap，會 freeze）
