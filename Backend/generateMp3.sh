#!/bin/bash
set -a
source .env
set +a

curl -X POST \
  'https://fgs-tts.sjailab.com/api/generate-tts' \
  -H 'Accept: audio/mpeg' \
  -H "Authorization: Bearer $FGS_TTS_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{
    "text": "各位大家好，今天很高興能和大家一起討論這個問題。",
    "speechVoicePresetName": "yikong",
    "textPreprocessorName": "sliced_by_chinese_grammar",
    "speed": 0.85
  }' \
  --output a2f_test_0.85_chinese.mp3