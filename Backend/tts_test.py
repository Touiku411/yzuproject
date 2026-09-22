import os
import requests
from dotenv import load_dotenv


load_dotenv()

tts_token = os.getenv("FGS_TTS_TOKEN")

tts_url = "https://fgs-tts.sjailab.com/api/generate-tts"


headers = {
    "Authorization": f"Bearer {tts_token}",
    "Content-Type": "application/json",
    "Accept": "audio/mpeg"
}


text = "各位大家好。今天很高興與大家見面。"


payload = {
    "text": text,
    "speechVoicePresetName": "yikong",
    "textPreprocessorName": "sliced_by_chinese_grammar",
    "speed": 1.0
}


response = requests.post(
    tts_url,
    headers=headers,
    json=payload,
    timeout=900
)


print("HTTP Status:", response.status_code)


if response.status_code != 200:
    print("❌ TTS API 發生錯誤")
    print(response.text)
    exit()


with open("output.mp3", "wb") as f:
    f.write(response.content)


print("✅ TTS 成功")
print("🎵 已產生 output.mp3")