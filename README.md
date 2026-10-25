# FreeCamRecord

Windows 區域錄影最小版本：**拉好區域 → 錄影 → 停止 → 自動儲存**。

## 使用

從 [GitHub Releases](https://github.com/tim12332000/FreeCamRecord/releases/latest) 下載 Windows x64 ZIP，解壓縮後開啟 `FreeCamRecord.exe`。獨立版已包含執行環境，不需另外安裝 .NET 或 FFmpeg。開發工作區的執行檔位於 `dist/FreeCamRecord.exe`。

- 拖曳上方工具列的空白處移動錄影框。
- 拉四邊或角落改變大小；點尺寸數字可輸入寬高（像素）。
- 拖到螢幕邊緣仍維持手動尺寸，不觸發 Windows 自動貼齊或上下填滿。
- 框內透明，可以正常操作底下程式，沒有畫面預覽。
- 按「錄影」開始，按「停止」後自動存檔。
- 預設資料夾為使用者的 `影片\FreeCamRecord`。完成時顯示「已儲存」，點它或從選單開啟資料夾。
- 選單可以設定格式、幀率、儲存位置、系統音效與麥克風。預設 MP4、自動幀率、系統音效開啟、麥克風關閉。
- 錄影中按關閉，會先停止並儲存再退出。
- 工具列使用不搶焦點的浮動視窗；開始及停止錄影時將焦點還給選區內的應用程式，避免會在失焦時暫停的網頁／遊戲因按錄影而停住。

## 格式與幀率

- **MP4**：H.264 影片、AAC 音訊，預設格式，檔案較小、方便播放與分享。
- **AVI**：MJPEG 影片、PCM 音訊。檔案較大，單檔約 1.8 GB 自動停止並儲存。
- 幀率選項：**自動、15、30、60、120 FPS**。
- **自動**在每次開始時讀取錄影框所在螢幕的目前更新率，例如 144 Hz → 144 FPS、165 Hz → 165 FPS。跨螢幕時依框覆蓋最多的螢幕判斷；讀取失敗使用 60 FPS，不修改 Windows 螢幕設定。
- 幀率是錄製目標，實際擷取速度受範圍大小、顯示卡、編碼器和電腦負載影響。這不是硬體 VSync 或可變更新率錄影；落後時重複影格維持影片時長。
- MP4 將奇數寬高在右／下補最多 1 像素黑邊，以符合 H.264 偶數尺寸；不拉伸原畫面。AVI 保留原尺寸。

## 限制

- 錄影中固定範圍，停止後才能移動或改變大小。
- 最小錄影尺寸為 312 × 120 像素，以保留工具列操作空間。
- 使用 Windows 預設播放／錄音裝置。裝置失效會停止並提示；可關閉對應音訊後錄製。
- 不支援受保護影片、鎖定畫面或 Windows 安全桌面的錄製。
- 沒有剪輯、播放器、直播、帳號或雲端上傳功能。

## 建置

使用 .NET 6 SDK（Windows Desktop）：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

沒有 NuGet 或第三方執行套件。桌面擷取優先使用 DXGI Desktop Duplication，直接從 GPU 複製選區；跨螢幕、旋轉或裝置不支援時使用 GDI 相容路徑。兩條路徑都重用像素緩衝區。音訊使用 WASAPI，MP4 使用 Windows Media Foundation，AVI 直接寫入 RIFF 容器。MP4 會允許系統選擇硬體編碼，設定失敗時嘗試軟體編碼；不保證每台電腦都使用 GPU 編碼。

程式 icon 及所有按鈕／選單線條圖示均為原創、本機繪製，無外部圖庫或圖示字型。重建多尺寸 icon：

```powershell
powershell -NoProfile -File .\tools\Build-Icons.ps1
```

## 驗證

在已登入的互動式 Windows 桌面執行，測試會短暫移動滑鼠、顯示測試視窗並播放測試音：

```powershell
$test = Start-Process .\dist\FreeCamRecord.exe -ArgumentList '--self-test' -PassThru -Wait
$test.ExitCode
```

結果、畫面截圖及實際錄製影片位於工作目錄的 `.omx/verification/<時間>/`。測試涵蓋真實拖曳／縮放、滑鼠穿透、停止存檔、奇數尺寸裁切、連續錄影不覆蓋、系統音效、麥克風啟動、錄影中關閉自動存檔、錯誤儲存路徑、圖示／選單與 MP4、120 FPS、自動更新率及 144 FPS AAC。`--media-test` 可單獨執行格式／幀率驗證，不拖曳滑鼠。

已驗證的環境：Windows 10 x64，單螢幕 150% 縮放。多螢幕混合 DPI、麥克風語音品質及長時間錄影另需對應環境驗證。

## 實際網頁流暢度驗證

`tools/motion-test.html` 是本機動畫網頁，包含四種速度的移動方塊、捲動文字和每次 requestAnimationFrame 更新的可見二進位影格編號。

`tools/browser-motion-test.mjs` 用獨立 Chrome 測試設定檔開啟網頁，再呼叫同一套錄影引擎錄製實際桌面中的瀏覽器視窗，不使用瀏覽器自身的錄影 API。`tools/analyze-motion.py` 從解碼後的影片讀回影格編號，計算不同來源影格／秒、重複率與停頓時間。開發驗證需要本機 Chrome、Node、FFmpeg 與 Python Pillow；這些不是應用程式執行依賴。

```powershell
node .\tools\browser-motion-test.mjs .\dist\FreeCamRecord.exe browser dxgi
```

0.3 版修正了 GDI 大區域高幀率錄影大量重複影格的問題。1350 × 1200、165 FPS 的同一網頁測試中，GDI 約 75 個不同影格／秒、約55%重複；DXGI 約164個不同影格／秒、低於1%重複。這是該電腦的短測結果，不代表每台硬體都能達到相同效能。

0.3.1 修正工具列與 WinForms 按鈕重新啟用時奪取來源視窗焦點。此版驗收直接啟動 `dist/FreeCamRecord.exe`，使用實際滑鼠拖曳／縮放、選單開啟系統音效、開始／停止按鈕，在沒有除錯或停用背景節流參數的普通 Chrome 中測試失焦會暫停的動畫頁，再用 Chrome 回放。30／60／120／自動165 FPS 均完成；逐格動作、音訊提示／畫面閃光時間與播放器指標保存在 `.omx/verification/final-matrix-*/`。可用 Chrome 開啟 `dist/Chrome-FPS-Test.html` 看實際驗收片。

此次驗收涵蓋 Chrome 與焦點敏感測試頁，不代表已對所有遊戲驗證。Chrome 播放器回呼可能合併，不能只憑回呼次數宣稱完美120 FPS。播放測試靜音，聲音存在及時間差由解碼後的音訊／畫面分析驗證。
