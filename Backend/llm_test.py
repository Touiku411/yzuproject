import os
import requests
from dotenv import load_dotenv


load_dotenv()

base_url = os.getenv("OPENWEBUI_BASE_URL")
api_key = os.getenv("OPENWEBUI_API_KEY")

model_id = "qwen3.6:35b-a3b-mtp-q8_0"

def ask_llm(user_text):
    url = f"{base_url}/api/chat/completions"

    headers = {
        "Authorization": f"Bearer {api_key}",
        "Content-Type": "application/json"
    }

    payload = {
        "model": model_id,
        "messages": [
            {
                "role": "system",
                "content": "你是一名溫和沉穩的大師，請使用繁體中文簡短回答。"
            },
            {
                "role": "user",
                "content": user_text
            }
        ],
        "stream": False
    }

    try:
        response = requests.post(
            url,
            headers=headers,
            json=payload,
            timeout=120
        )

        response.raise_for_status()

    except requests.exceptions.Timeout:
        print("LLM 回應超時")
        return None

    except requests.exceptions.RequestException as e:
        print(f"LLM API 錯誤：{e}")
        return None
        
    data = response.json()

    reply = data["choices"][0]["message"]["content"].strip()
    return reply

user_text = input("你:")

reply = ask_llm(user_text)

print(f"回應：{reply}")