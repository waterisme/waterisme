# 版本紀錄

## v1.11 — 2026-06-19

**功能：剪貼簿支援圖片 + 完整廣播到所有連線**
- `ClipboardData` payload 前置 1 byte 類型旗標（0=文字，1=圖片 PNG），
  新增 `Msg.ClipboardText` / `Msg.ClipboardImage` / 新版 `ParseClipboard`。
- `ClipboardSync` 改為同時輪詢文字與圖片：
  - 圖片以「原始像素 FNV-1a hash」判斷是否變化（LockBits 直接轉 32bppArgb，
    對 PNG 重新編碼穩定，不會因壓縮位元組不同而誤判 → 不會 echo 迴圈）。
  - 新增 `SetRemoteText` / `SetRemoteImage`；設定後同步更新 last 簽章避免回傳。
  - 移除舊的 sticky `_suppress` 旗標（會吞掉遠端設定後的第一次本機複製）。
- 廣播為既有架構自然行為：N 個連線各自有獨立 `ClipboardSync` 輪詢同一份本機
  剪貼簿，因此在 master 或任一 slave 複製（文字或圖片），會傳到其餘所有端。

**修正：Ctrl/Shift/Alt/CapsLock/Tab/方向鍵 無法完整遙控**
- 主控端鍵盤改用 `IMessageFilter` 攔截 `WM_(SYS)KEYDOWN/UP` 原始訊息，取代
  原本的 `KeyDown/KeyUp` 事件。原事件會漏鍵：Alt/F10 走 `WM_SYSKEYDOWN`
  被選單迴圈吃掉；Tab/方向鍵被對話框導覽（ProcessDialogKey）攔截，根本進不到
  KeyDown。改用訊息過濾後所有鍵都乾淨轉發，且本機不會被 Alt 開選單、Alt+F4
  關視窗、Tab 跑焦點（僅 F11/Esc 保留為本機快捷）。
- `InputSimulator.KeyEvent` 對方向鍵/Insert/Delete/Home/End/PgUp/PgDn/
  右側修飾鍵/Win 鍵補上 `KEYEVENTF_EXTENDEDKEY`，注入更正確。

**修正：slave 端修飾鍵「卡住」（Ctrl/Shift 放不掉）**
- 主控端視窗失焦（`Deactivate`）或關閉時，自動補送所有「已按下未放開」鍵的
  key-up（`MonitorWindow._pressedKeys` 追蹤）。
- 被控端在連線結束時呼叫 `InputSimulator.ReleaseModifiers()` 當安全網，強制
  放開 Shift/Ctrl/Alt/Win（左右皆含），避免 master 中途斷線留下卡鍵。

**清理：移除未使用的 SharpDX 套件參考**
- `RemoteDesktop.csproj` 仍掛著 `SharpDX` / `SharpDX.DXGI`，但擷取早已改純
  Win32 BitBlt、原始碼零引用 → 移除，避免還原失敗與舊錯誤誤導。

**建置：新增 GitHub Actions 自動打包**
- `.github/workflows/build.yml`：windows runner 上 `dotnet publish` 出
  single-file `RemoteDesktop.exe`，上傳為 artifact 供下載。

## v1.10 — 2026-05-22

> （此版原誤標為 v2.0；版本號規則改為小改累加 patch，重大才跳大版本，故修正為 1.10。）

**UI：CustomPinDialog 在暗底配色下字看不到**
- `儲存`/`取消` 按鈕原本沒設配色 → 在 dark form (RGB 30,30,30) 上採系統預設
  按鈕色，文字易與背景同色不易閱讀。改為：
  - 儲存：藍底白字 (RGB 0,120,215) + FlatStyle.Flat（與 ConnectDialog 一致）
  - 取消：深灰底白字 (RGB 70,70,75) + FlatStyle.Flat
- PIN 輸入框改深底白字 + 單線邊框，與整體暗色主題統一。

## v1.9 — 2026-05-21

**UX：連線歷史顯示「最後使用 PIN」**
- `ConnectionEntry` 加 `LastPin` 欄位，持久化到 `%APPDATA%\RemoteDesktop\history.json`
  （舊版 history.json 無此欄位 → 反序列化為 null，向後相容）。
- `ConnectionHistory.AddOrUpdate(nick, ip, pin, port)`：未帶 pin 時保留舊紀錄的 pin。
- ConnectDialog 歷史清單顯示 `暱稱 (IP)  [紅]` 或 `[●●●]`（自訂 PIN 遮罩避免肩窺）。
- 選歷史項目時，自訂 PIN 自動填入 PIN textbox；顏色 PIN 留給使用者點顏色按鈕。
- 連線成功（顏色或 PIN）都會更新該紀錄的 LastPin，下次直接快速重連。

**UX：Slave「清除自訂 PIN」按鈕**
- `CustomPinDialog` 在已設定 PIN 時多顯示一顆紅色「清除」按鈕（左側），按下會跳確認後直接清除。
- 取消了「必須輸入空字串才能清除」的隱藏操作（仍保留留空 OK 的舊行為當 fallback）。

## v1.8 — 2026-05-19

**Bug 修正：v1.7 整個畫面 freeze**
- `DxgiCapture` 在 BitBlt 之後呼叫 `DrawIconEx` 把游標畫到 bitmap 上會阻塞，
  造成擷取迴圈卡住，下游 tile diff 完全沒辦法運作。
- 完全撤掉 capture 路徑的游標繪製。

**Bug 修正：v1.7 tile diff 第一幀後就停 (症狀 B)**
- 真正的 root cause：`MasterClient.HandleScreenData` 用 `new Bitmap(MemoryStream)`，
  然後在 `using` 結尾 dispose 那個 stream。`Bitmap(stream)` 的契約是「stream
  必須在 Bitmap 生命週期內保持開啟」，dispose 後 canvas 變失效，第二次以後
  `Graphics.FromImage(canvas)` 拿到的就是 broken bitmap，tile 永遠繪不出來。
- 修法：改用 `new Bitmap(Image.FromStream(ms))` 做一份**獨立 copy**，stream 可
  安全 dispose。

**Bug 修正：tile diff 的 pixel format 不一致**
- `DxgiCapture` 改成 `Format32bppArgb` 與縮小路徑（預設就是 Argb）統一格式。
- `ExtractPixels` 改用 `bmp.PixelFormat`（native）而非寫死 `Argb`，避免每幀
  GDI+ 格式轉換造成 byte sequence 不確定。

**新功能：游標獨立串流（v1.7 的 overlay 失敗後的替代方案）**
- 新 message `CursorUpdate (0x10)`：
  ```
  monitor | x | y | visible (bool) | hotspotX | hotspotY | pngLen | png?
  ```
  png 長度 = 0 表示「形狀沒變，用快取的」。
- Slave 開獨立 `CursorLoop` thread（~30 Hz）：
  - 用 `GetCursorInfo` 取得位置 + cursor handle
  - 位置縮放到「frame 座標」（與 master 收到的縮小圖一致）
  - 只在位置變、形狀變、或重新出現時才送
  - 形狀變化時用 `Icon.FromHandle().ToBitmap()` 編成 PNG（含 hotspot）
- Master 收到後存進 `MonitorWindow._cursorShape` 並 `Invalidate()`；
  `OnCanvasPaint` 在畫完螢幕後依當前 letterbox/stretch 縮放繪製游標。
- 優點：靜態畫面時游標仍順、tile diff 不會被游標移動干擾、capture 不會 freeze。

## v1.7 — 2026-05-19

**Bug 修正：關閉最後一個視窗會 crash**
- `MainForm.cs` 在 `FormClosed` 事件**內**同步呼叫 `Disconnect`，會反過來
  對「正在關閉的這個視窗」呼叫 `w.Close()`，造成 WinForms reentry 並當掉。
- 改用 `BeginInvoke` 把 `Disconnect` 延後到 FormClosed 結束後執行。
- `Connection.Disconnecting` 旗標 + `Disconnect()` 開頭 idempotent guard 防重入。

**Bug 修正：master 看不到 slave 的游標**
- Win32 `BitBlt`（即使加 `CAPTUREBLT`）不會擷取游標 — 它是 OS overlay。
- `DxgiCapture.Capture()` 在 `BitBlt` 之後加 `GetCursorInfo` + `DrawIconEx`
  把游標畫到 bitmap 上，再編碼成 JPEG / tile。
- 處理 hotspot 偏移，並過濾掉跨螢幕的游標。
- GDI handle（`hbmMask`、`hbmColor`）正確以 `DeleteObject` 釋放。

**UX：ConnectDialog 按 Enter 觸發「用 PIN 連線」**
- 設 `AcceptButton = pinConnectBtn`。

**新功能：Tile 差異傳輸（Step 2 — 64×64 dirty rectangles）**
- 新 message `ScreenTiles (0x0F)`：`monitor | frameW | frameH | tileCount |
  [tileX, tileY, tileW, tileH, jpegLen, jpeg]*`（座標 int16，JPEG 長度 int32）。
- Slave (`CaptureLoop`) 邏輯：
  - 維護上一幀的 raw BGRA pixel buffer。
  - 每幀 frame hash 與前一幀比，相同就完全跳過。
  - hash 不同 → 切 64×64 比對每塊（`Span<byte>.SequenceEqual`），只編碼有變動的塊。
  - 變動 tile 數 > 50% 或每 300 幀（~10 s）強制送一張完整 JPEG（keyframe），
    避免 master 累積偏差。
- Master (`MasterClient`)：
  - 維護 `Dictionary<monitor, Bitmap>` 當 canvas。
  - 收 `ScreenData`：替換 canvas，clone 給 `MonitorWindow` 顯示。
  - 收 `ScreenTiles`：用 `Graphics.DrawImage` 把 tile blit 到 canvas，clone 給 UI。
  - 若收到 tile 但無 canvas（master 還沒收到第一張 keyframe）→ 安全丟棄。
- 多 master 場景：每個 master 各自的 `MasterClient` 維護自己的 canvas；
  slave 端的 tile 狀態（`prevPixels` 等）也是 per-session 的 local variable，互不干擾。

## v1.6 — 2026-05-19
**Bug 修正：視窗全部關閉沒真的斷線（v1.5 修不徹底）**
- v1.5 用 `IsDisposed` 檢查在 `FormClosed` 事件中永遠是 `false`（dispose 在事件之後才發生），所以「全部關完就 Disconnect」根本沒觸發。
- 改用 `Connection.OpenWindows` 計數器，每關一個視窗就 `--`，歸 0 時 `Disconnect`。

**新功能：Slave 長期運行健全性（Level 1）**
- TCP keepalive：30 s idle 後開始 probe、5 s 重試 → 死掉的 master 約 50 s 內偵測到。
- `HashSet<TcpClient> + lock` 取代 `ConcurrentBag<TcpClient>`（後者無法移除元素，會累積殭屍引用）。
- SlaveForm 加「運行時間、累計連線次數」即時顯示。

**新功能：應用層 Keepalive（Level 2）**
- Slave 每 20 s 主動送 `Ping`；Master 收到回 `Pong`。
- Slave socket `ReadTimeout = 90 s`，超時直接斷線。
- 連線結束時 `GC.Collect()` + `WaitForPendingFinalizers()` 確保 GDI/Bitmap 立刻釋放，避免長期運行記憶體爬升。
- `Protocol.cs` 加 `Msg.Ping() / Msg.Pong()` helpers；`MasterClient.ReceiveLoop` 處理 `Ping`。

**新功能：自訂 PIN（長期放著用）**
- SlaveForm 加「設定 PIN」按鈕 → 對話框輸入 4–32 字元（留空可清除）。
- 設定存到 `%APPDATA%\RemoteDesktop\slave.json`，slave 重啟自動讀回。
- 顏色色塊**仍照常顯示**——slave 同時接受「色塊」或「自訂 PIN」兩種登入。
- Master ConnectDialog 在 5 顆顏色鈕下方加「輸入 PIN」欄位 + 「用 PIN 連線」按鈕。
- 為長時間放著等候連線而設計：不用每次跑去看 slave 螢幕的顏色。
- 新增 `Core/SlaveConfig.cs` 用 `System.Text.Json` 做設定持久化。

## v1.5 — 2026-05-19
**Bug 修正**
- 修 `MainForm.CreateMonitorWindows` 的 C# closure bug：`for` 迴圈裡 `i` 被多個 lambda 共用，導致關閉視窗時送出錯誤的 monitor index，slave 沒有真的暫停串流，所以流量還是很大。
- 所有遠端視窗都關閉時，自動 Disconnect 整個連線（不再只是 pause 個別 monitor）。

**新功能**
- SlaveForm 啟動時偵測自己是否以系統管理員身分執行：
  - 不是 admin → 顯示黃色警告 + 副文字「工作管理員等高權限程式將無法操作」 + 橘色「以系統管理員身分重啟」按鈕（按下觸發 UAC）。
  - Master 不受影響，不會跳 UAC。

## v1.4 — 2026-05-19
**新功能：差異偵測（Step 1 of 頻寬最佳化）**
- Slave 用 FNV-1a hash（每 256 byte 取樣）算每幀的指紋；跟上一幀比對相同就不編碼、不傳。
- 靜態畫面流量趨近 0（之前每秒 10 MB 都在送沒變的圖）。
- Master 視窗標題列即時顯示 FPS（每秒重算一次），全螢幕 overlay 也顯示。

## v1.3 — 2026-05-19
**Bug 修正：擷取與顯示**
- `Program.cs` 設 `HighDpiMode.PerMonitorV2`，讓 `Screen.Bounds` 在高 DPI 螢幕回傳物理像素而不是邏輯像素。
- `DxgiCapture` 改用 Win32 `BitBlt` 直接擷取物理像素，取代有 DPI 問題的 `Graphics.CopyFromScreen`。
- 解決 2880×1920 螢幕只擷取到 2/3 的 bug。

**Bug 修正：閃爍**
- 新增 `BufferedPanel : Panel` subclass，啟用 `OptimizedDoubleBuffer + AllPaintingInWmPaint + UserPaint + ResizeRedraw`，消除每幀重繪的閃爍。

> 此版本之後打了 `v1.3-stable` 標籤，並開新分支 `experiment-h264` 給後續實驗。

## v1.2 — 2026-05-14
**Bug 修正：畫面只顯示部分**
- 把 `PictureBox` 整個換掉，改用 `Panel` + 自訂 `Paint` 事件渲染。
- 自訂的 `scale = Math.Min(寬比, 高比)` 確保任何遠端解析度都會正確縮放填滿視窗。
- `PictureBoxSizeMode.Zoom` 在 .NET 8 有 DPI 渲染 bug，當遠端圖比視窗大時會以 1:1 顯示（裁切）而不是縮放。

## v1.1 — 2026-05-14
**新功能：顏色 PIN（取代數字 PIN）**
- Slave 啟動時隨機從紅/藍/黃/黑/白 五色中選一，視窗顯示大色塊。
- Master ConnectDialog 立刻顯示 5 個彩色按鈕，使用者點對應顏色即可連線（不再需要事先取得 PIN 的網路步驟）。
- `PinColors` helper 加入 `Protocol.cs`。

**新功能：多 Master 連線**
- `SlaveServer` 完全重構：`ServerLoop` 接受連線後立即開新執行緒處理，繼續等待下一個。
- 每個連線有獨立的 `monitorPaused[]` 狀態。
- `ConcurrentBag<TcpClient>` 追蹤所有連線。
- `ClientConnected` / `ClientDisconnected` 事件傳入當前連線數，被控端視窗顯示「● N 個主控端連線中」。

## v1.0 — 2026-05-12（初始版本）
- C# .NET 8 WinForms 遠端桌面應用。
- 從 .NET Framework 升級到 .NET 8。
- SharpDX 在 .NET 8 不相容 → 改用 GDI+ `CopyFromScreen` 擷取螢幕（後來 v1.3 又改成 BitBlt）。
- TCP 自訂二進位協議：`[int32 bodyLen][byte type][payload]`。
- 多螢幕支援：每個遠端螢幕一個 `MonitorWindow`。
- 滑鼠 / 鍵盤 / 滾輪轉發。
- 雙向剪貼簿同步。
- 連線記錄（暱稱、IP、port）。
- 4 位數字 PIN 驗證（v1.1 改顏色）。
- 數字 PIN 由使用者手動輸入（v1.1 改自動產生 + 點按）。
