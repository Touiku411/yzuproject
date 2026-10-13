import json
import os
import threading
import time

import requests
from dotenv import load_dotenv
from fastapi import FastAPI, HTTPException, Response
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
    "content": """
你是一名溫和沉穩的大師，使用繁體中文簡短回答。
只輸出 JSON：{"text":"回答","emotion":"neutral"}
emotion 只能是 neutral、happy、sad、angry、relaxed，
代表你回答時的表達情緒。一般解說使用 neutral。
""".strip()
}

MAX_CONVERSATION_TURNS = 10

ALLOWED_EMOTIONS = {
    "neutral", "happy", "sad", "angry", "relaxed"
}

conversation_history = [SYSTEM_MESSAGE.copy()]
conversation_lock = threading.Lock()


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
    with conversation_lock:
        conversation_history.clear()
        conversation_history.append(SYSTEM_MESSAGE.copy())


def parse_llm_reply(raw_reply):
    if not isinstance(raw_reply, str):
        raise ValueError("LLM 回覆內容不是文字")

    content = raw_reply.strip()

    # 容許模型偶爾加上 ```json ... ```。
    if content.startswith("```"):
        lines = content.splitlines()
        if len(lines) >= 2 and lines[-1].strip() == "```":
            content = "\n".join(lines[1:-1]).strip()

    try:
        result = json.loads(content)
    except json.JSONDecodeError:
        # 一般文字回覆退回中性；損壞的 JSON 則停止處理。
        if not content or content.startswith(("{", "[", "```")):
            raise ValueError("LLM 沒有回傳有效的情緒 JSON")

        print("[情緒] LLM 回傳一般文字，使用 neutral")

        return {
            "text": content,
            "emotion": "neutral"
        }

    if not isinstance(result, dict):
        raise ValueError("LLM 回覆必須是 JSON 物件")

    text = result.get("text")
    if not isinstance(text, str) or not text.strip():
        raise ValueError("LLM 回覆缺少有效的 text")

    emotion = result.get("emotion", "neutral")

    if not isinstance(emotion, str):
        emotion = "neutral"

    emotion = emotion.strip().lower()

    if emotion not in ALLOWED_EMOTIONS:
        print(f"[情緒] 不支援 {emotion!r}，使用 neutral")
        emotion = "neutral"

    return {
        "text": text.strip(),
        "emotion": emotion
    }


def ask_llm(user_text):
    if not base_url or not api_key:
        raise ValueError(
            "請在 .env 設定 OPENWEBUI_BASE_URL 和 OPENWEBUI_API_KEY"
        )

    url = f"{base_url.rstrip('/')}/api/chat/completions"

    headers = {
        "Authorization": f"Bearer {api_key}",
        "Content-Type": "application/json"
    }

    with conversation_lock:
        messages = conversation_history + [
            {"role": "user", "content": user_text}
        ]

        payload = {
            "model": model_id,
            "messages": messages,
            "stream": False
        }

        started = time.perf_counter()

        response = requests.post(
            url,
            headers=headers,
            json=payload,
            timeout=120
        )
        response.raise_for_status()

        elapsed = time.perf_counter() - started
        print(f"LLM 請求耗時：{elapsed:.2f} 秒")

        data = response.json()
        raw_reply = data["choices"][0]["message"]["content"]

        reply = parse_llm_reply(raw_reply)

        conversation_history.append({
            "role": "user",
            "content": user_text
        })

        # 對話紀錄也只保留 text、emotion。
        conversation_history.append({
            "role": "assistant",
            "content": json.dumps(reply, ensure_ascii=False)
        })

        trim_conversation_history()

        turn_count = (len(conversation_history) - 1) // 2
        print(f"目前保留 {turn_count}/{MAX_CONVERSATION_TURNS} 輪對話")
        print(f"[情緒] {reply['emotion']}")

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
        "speed": 0.9
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
    return {"status": "ok"}


@app.post("/chat")
def chat(request: ChatRequest):
    print(f"Unity 傳來：{request.text}")

    try:
        result = ask_llm(request.text)

    except requests.RequestException as exc:
        print(f"LLM 請求失敗：{exc}")
        raise HTTPException(
            status_code=502,
            detail="LLM 請求失敗"
        ) from exc

    except (ValueError, KeyError, IndexError, TypeError) as exc:
        print(f"LLM 回覆或設定錯誤：{exc}")
        raise HTTPException(
            status_code=502,
            detail="LLM 回覆或設定錯誤"
        ) from exc

    print(f"LLM 回答：{result['text']}")

    return {
        "reply": result["text"],
        "emotion": result["emotion"]
    }


@app.post("/tts")
def tts(request: TTSRequest):
    print(f"準備合成語音：{request.text}")

    audio_data = generate_tts(request.text)

    print(f"TTS 完成，MP3 大小：{len(audio_data)} bytes")

    return Response(
        content=audio_data,
        media_type="audio/mpeg"
    )


@app.post("/clear-history")
def clear_history():
    clear_conversation_history()
    print("對話紀錄已清除")

    return {
        "status": "ok",
        "message": "Conversation history cleared"
    }