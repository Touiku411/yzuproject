#!/bin/bash
set -a
source .env
set +a

# 檢查是否有安裝 jq 工具，用於安全產生 JSON
if ! command -v jq &> /dev/null; then
    echo "錯誤: 請先安裝 jq 工具 (例如: brew install jq 或 apt install jq)"
    exit 1
fi

# 檢查是否帶入文字檔案參數
if [ -z "$1" ]; then
    echo "使用方法: $0 <文字檔案路徑>"
    echo "範例: $0 input.txt"
    exit 1
fi

INPUT_FILE="$1"

# 自動讀取檔案內容並包裝成合法的 JSON 字串（會自動處理換行 \n）
JSON_DATA=$(jq -n \
  --arg text "$(cat "$INPUT_FILE")" \
  --arg voice "yikong" \
  --arg preprocessor "sliced_by_chinese_grammar" \
  --argjson speed 0.85 \
  '{text: $text, speechVoicePresetName: $voice, textPreprocessorName: $preprocessor, speed: $speed}')

# 發送 API 請求
curl -X POST \
  'https://fgs-tts.sjailab.com/api/generate-tts' \
  -H 'Accept: audio/mpeg' \
  -H "Authorization: Bearer $FGS_TTS_TOKEN" \
  -H 'Content-Type: application/json' \
  -d "$JSON_DATA" \
  --output a2f_test1.mp3

echo "轉換完成，音檔已儲存至 a2f_test1.mp3"