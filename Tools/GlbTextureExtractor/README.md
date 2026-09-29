# GlbTextureExtractor

## Unity 一次性批次匯出

1. 在 Unity 開啟 **Tools → Network Example → GLB Texture Extractor...**。
2. 第一次使用先按 **Prepare Environment**，建立工具專用 `.venv` 並安裝 Pillow（需網路）。電腦須先有 Python 3.10+；找不到 Python 時，可在視窗填入 Python 執行檔的完整路徑。
3. 按 **Open input**，將所有 GLB 放進 `Tools/GlbTextureExtractor/input/`。
4. 按 **Batch Export**，一次匯出所有 GLB；完成後開啟 `Tools/GlbTextureExtractor/output/`。

每個 GLB 產生一個同名 ZIP，例如 `input/robot.glb` → `output/robot.zip`。只掃描 input 的第一層，副檔名不區分大小寫。output 僅保留 ZIP，不留下解壓的 PNG 資料夾。輸入、輸出與虛擬環境不加入 Git。

重跑會以成功的結果取代同名 ZIP；失敗時保留舊 ZIP，繼續處理其他 GLB，並在 Unity Console 顯示錯誤。舊 ZIP 可能不是本次輸入的結果，請以 Console 成功紀錄為準。不同輸入若產生不分大小寫的同名 ZIP，會先停止並要求重新命名。未對應本次輸入的既有 ZIP 不會刪除。

也可從命令列進行同樣的批次處理（預設使用工具內的 input/output）：

```sh
Tools/GlbTextureExtractor/.venv/bin/python Tools/GlbTextureExtractor/extract_glb_textures.py --batch
# 自訂資料夾
python Tools/GlbTextureExtractor/extract_glb_textures.py --batch /path/to/input -o /path/to/output
```

## 單檔命令列模式

需要 Python 3.10 以上及 Pillow。於專案根目錄執行：

```sh
python3 -m venv .venv-glb-textures
source .venv-glb-textures/bin/activate
python -m pip install -r Tools/GlbTextureExtractor/requirements.txt

# 匯出到 model_textures 資料夾
python Tools/GlbTextureExtractor/extract_glb_textures.py model.glb

# 指定輸出資料夾，並額外建立 exported_textures.zip
python Tools/GlbTextureExtractor/extract_glb_textures.py model.glb -o exported_textures --zip

# 自訂 ZIP 路徑
python Tools/GlbTextureExtractor/extract_glb_textures.py model.glb -o exported_textures --zip textures.zip
```

- 匯出 GLB `images` 中所有圖片；多個 texture 共用同一張 image 時僅匯出一次。
- 每張 PNG 固定 **1024×1024**，以 Lanczos 縮放；非正方形會拉伸，不裁切或補邊。
- 每張檔案嚴格 **小於 2,000,000 bytes**，ZIP 的總大小不受此限制。
- 先嘗試完整 RGB/RGBA PNG 壓縮，超限才減色；減色可能影響顏色與透明度精度，執行結果會標示色彩數量。透明背景會保留，不會轉成白底。
- 支援 BIN bufferView、data URI，以及位於 GLB 所在目錄內的相對圖片／buffer 路徑。不下載網路資源。
- 支援 Pillow 能解碼的圖片（例如 PNG、JPEG、WebP）。KTX2/BasisU 需先使用其他工具解碼；不支援的格式會回報失敗，不會靜默略過。
- 保留原圖片方向，不做 UV 翻轉或材質烘焙；不拆分 metallic/roughness 等共用通道。
- 輸出名稱使用 image 索引與安全化名稱，避免同名覆蓋。單檔模式的輸出資料夾與 ZIP 必須尚未存在；批次模式會取代成功匯出的同名 ZIP。
- 所有圖片轉換成功後才產生輸出資料夾。若 ZIP 寫入失敗，已匯出的 PNG 仍保留。

執行驗證：

```sh
python -m unittest discover -s Tools/GlbTextureExtractor -p 'test_*.py'
```
