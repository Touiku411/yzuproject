import os
import requests

from dotenv import load_dotenv
from fastapi import FastAPI, Response
from pydantic import BaseModel


load_dotenv()

base_url = os.getenv("OPENWEBUI_BASE_URL")
api_key = os.getenv("OPENWEBUI_API_KEY")
tts_token = os.getenv("FGS_TTS_TOKEN")

model_id = "qwen3.6:35b-a3b-mtp-q8_0"
tts_url = "https://fgs-tts.sjailab.com/api/generate-tts"

app = FastAPI()

SYSTEM_MESSAGE = {
    "role": "system",
    "content": "你是一名溫和沉穩的大師，請使用繁體中文簡短回答。"
}

MAX_CONVERSATION_TURNS = 10

conversation_history = [
    SYSTEM_MESSAGE.copy()
]

class ChatRequest(BaseModel):
    text: str
class TTSRequest(BaseModel):
    text: str

def trim_conversation_history():
    max_history_messages = MAX_CONVERSATION_TURNS * 2

    if len(conversation_history) > 1 + max_history_messages:
        recent_messages = conversation_history[-max_history_messages:]

        conversation_history.clear()
        conversation_history.append(SYSTEM_MESSAGE.copy())
        conversation_history.extend(recent_messages)

def clear_conversation_history():
    conversation_history.clear()
    conversation_history.append(
        SYSTEM_MESSAGE.copy()
    )

def ask_llm(user_text):
    url = f"{base_url}/api/chat/completions"

    headers = {
        "Authorization": f"Bearer {api_key}",
        "Content-Type": "application/json"
    }

    messages = conversation_history + [
        {
            "role": "user",
            "content": user_text
        }
    ]
    
    payload = {
        "model": model_id,
        "messages": messages
    }

    response = requests.post(
        url,
        headers=headers,
        json=payload,
        timeout=120
    )

    response.raise_for_status()

    data = response.json()

    reply = data["choices"][0]["message"]["content"].strip()

    conversation_history.append(
        {
            "role": "user",
            "content": user_text
        }
    )

    conversation_history.append(
        {
            "role": "assistant",
            "content": reply
        }
    )

    trim_conversation_history()

    turn_count = (len(conversation_history) - 1) // 2

    print(
        f"📚 目前保留 {turn_count}/{MAX_CONVERSATION_TURNS} 輪對話"
    )

    print("📚 目前對話紀錄：")

    for message in conversation_history:
        print(
            message["role"],
            ":",
            message["content"]
        )

    return reply

def generate_tts(text):
    headers = {
        "Authorization": f"Bearer {tts_token}",
        "Content-Type": "application/json",
        "Accept": "audio/mpeg"
    }

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

    response.raise_for_status()

    return response.content
@app.get("/")
def root():
    return {
        "status": "ok"
    }


@app.post("/chat")
def chat(request: ChatRequest):

    print(f"🗣️ Unity 傳來：{request.text}")

    reply = ask_llm(request.text)

    print(f"🧠 LLM 回答：{reply}")

    return {
        "reply": reply
    }


@app.post("/tts")
def tts(request: TTSRequest):

    print(f"🔊 準備合成語音：{request.text}")

    audio_data = generate_tts(request.text)

    print(
        f"✅ TTS 完成，MP3 大小：{len(audio_data)} bytes"
    )

    return Response(
        content=audio_data,
        media_type="audio/mpeg"
    )


@app.post("/clear-history")
def clear_history():
    clear_conversation_history()

    print("🧹 對話紀錄已清除")

    return {
        "status": "ok",
        "message": "Conversation history cleared"
    }