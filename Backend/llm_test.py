import json
import os
import time

import requests
from dotenv import load_dotenv


load_dotenv()

base_url = os.getenv("OPENWEBUI_BASE_URL")
api_key = os.getenv("OPENWEBUI_API_KEY")

model_id = "qwen3.6:35b-a3b-mtp-q8_0"

ALLOWED_EMOTIONS = {
    "neutral", "happy", "sad", "angry", "relaxed"
}

SYSTEM_PROMPT = """
你是一名溫和沉穩的大師，使用繁體中文簡短回答。
只輸出 JSON：{"text":"回答","emotion":"neutral"}
emotion 只能是 neutral、happy、sad、angry、relaxed，
代表你回答時的表達情緒。一般解說使用 neutral。
""".strip()


def parse_reply(raw_reply):
    if not isinstance(raw_reply, str):
        raise ValueError("LLM 回覆內容不是文字")

    content = raw_reply.strip()

    # 容許模型偶爾加上 ```json ... ```。
    if content.startswith("```"):
        lines = content.splitlines()
        if len(lines) >= 2 and lines[-1].strip() == "```":
            content = "\n".join(lines[1:-1]).strip()

    result = json.loads(content)

    if not isinstance(result, dict):
        raise ValueError("回覆必須是 JSON 物件")

    text = result.get("text")
    if not isinstance(text, str) or not text.strip():
        raise ValueError("text 必須是非空文字")

    emotion = result.get("emotion")
    if not isinstance(emotion, str):
        raise ValueError("emotion 必須是文字")

    emotion = emotion.strip().lower()

    if emotion not in ALLOWED_EMOTIONS:
        raise ValueError(f"不支援的 emotion：{emotion}")

    return {
        "text": text.strip(),
        "emotion": emotion
    }


def ask_llm(user_text):
    url = f"{base_url.rstrip('/')}/api/chat/completions"

    headers = {
        "Authorization": f"Bearer {api_key}",
        "Content-Type": "application/json"
    }

    payload = {
        "model": model_id,
        "messages": [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": user_text}
        ],
        "stream": False
    }

    started = time.perf_counter()

    try:
        response = requests.post(
            url,
            headers=headers,
            json=payload,
            timeout=120
        )
        response.raise_for_status()

        elapsed = time.perf_counter() - started
        print(f"\nLLM 請求耗時：{elapsed:.2f} 秒")

        data = response.json()
        raw_reply = data["choices"][0]["message"]["content"]

        print("\nLLM 原始輸出：")
        print(raw_reply)

        return parse_reply(raw_reply)

    except requests.exceptions.Timeout:
        print("LLM 回應超時")

    except requests.exceptions.RequestException as exc:
        print(f"LLM API 錯誤：{exc}")

    except (ValueError, KeyError, IndexError, TypeError) as exc:
        print(f"回覆格式錯誤：{exc}")

    return None


def main():
    if not base_url or not api_key:
        print("請在 .env 設定 OPENWEBUI_BASE_URL 和 OPENWEBUI_API_KEY")
        return

    print("輸入 q 結束；每次測試都是獨立對話。")

    while True:
        try:
            user_text = input("\n你：").strip()
        except (EOFError, KeyboardInterrupt):
            print("\n測試結束。")
            break

        if user_text.lower() == "q":
            break

        if not user_text:
            continue

        reply = ask_llm(user_text)

        if reply is not None:
            print("\n解析成功：")
            print(f"回應：{reply['text']}")
            print(f"情緒：{reply['emotion']}")


if __name__ == "__main__":
    main()