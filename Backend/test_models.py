import os
import requests
from dotenv import load_dotenv


# 讀取 .env
load_dotenv()

base_url = os.getenv("OPENWEBUI_BASE_URL")
api_key = os.getenv("OPENWEBUI_API_KEY")


# API endpoint
url = f"{base_url}/api/models"


# HTTP Header
headers = {
    "Authorization": f"Bearer {api_key}"
}


# 發送 GET Request
response = requests.get(
    url,
    headers=headers,
    timeout=30
)


print("HTTP Status:", response.status_code)

if response.status_code == 200:
    data = response.json()

    print("✅ 成功取得模型列表")

    for model in data["data"]:
        print(
            "Model ID:",
            model["id"]
        )

else:
    print("❌ API 錯誤")
    print(response.text)