# 版本紀錄

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
