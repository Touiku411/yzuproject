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
    "text": ""Oh my gosh, is that the time already? I completely overslept! Where on earth are my car keys? I am going to be so incredibly late today!"。",
    "speechVoicePresetName": "yikong",
    "textPreprocessorName": "sliced_by_chinese_grammar",
    "speed": 0.85
  }' \
  --output a2f_test1.mp3
