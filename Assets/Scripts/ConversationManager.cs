using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.Threading.Tasks;

public class ConversationManager : MonoBehaviour
{
    [Serializable]
    private class ChatRequest
    {
        public string text;
    }

    [Serializable]
    private class ChatResponse
    {
        public string reply;
    }

    [Serializable]
    private class TTSRequest
    {
        public string text;
    }
    
    [Header("A2F")]
    [SerializeField]
    private A2FController a2fController;

    [Header("STT")]
    public STTReceiver sttReceiver;


    [Header("LLM")]
    [SerializeField]
    private string llmUrl =
        "http://127.0.0.1:8000/chat";

    [Header("TTS")]
    [SerializeField]
    private string ttsUrl =
        "http://127.0.0.1:8000/tts";

    [SerializeField]
    private AudioSource ttsAudioSource;

    [SerializeField]
    private string clearHistoryUrl =
        "http://127.0.0.1:8000/clear-history";

    void Start()
    {
        if (sttReceiver != null)
        {
            sttReceiver.OnTranscriptReceived +=
                HandleTranscript;
        }
        else
        {
            Debug.LogError(
                "❌ ConversationManager 沒有設定 STTReceiver！"
            );
        }
    }

    public void ClearConversation()
    {
        StartCoroutine(
            SendClearHistoryRequest()
        );
    }

    private IEnumerator SendClearHistoryRequest()
    {
        using (UnityWebRequest request =
            new UnityWebRequest(clearHistoryUrl, "POST"))
        {
            request.downloadHandler =
                new DownloadHandlerBuffer();

            yield return request.SendWebRequest();

            if (request.result !=
                UnityWebRequest.Result.Success)
            {
                Debug.LogError(
                    $"❌ 清除對話失敗：{request.error}"
                );

                yield break;
            }

            Debug.Log("🧹 對話紀錄已清除！");
        }
    }
    private bool isBusy = false;
    private void HandleTranscript(string text)
    {
        if (isBusy)
        {
            Debug.LogWarning(
                $"⏳ 大師正在回答，暫時忽略：{text}"
            );

            return;
        }

        Debug.Log(
            $"🧠 ConversationManager 收到：{text}"
        );

        isBusy = true;

        StartCoroutine(
            SendToLLM(text)
        );
    }


    private IEnumerator SendToLLM(string text)
    {
        ChatRequest chatRequest =
            new ChatRequest
            {
                text = text
            };

        string json =
            JsonUtility.ToJson(chatRequest);

        byte[] bodyRaw =
            Encoding.UTF8.GetBytes(json);

        Debug.Log(
            $"📤 傳送給 LLM：{json}"
        );


        using (UnityWebRequest request =
               new UnityWebRequest(llmUrl, "POST"))
        {
            request.uploadHandler =
                new UploadHandlerRaw(bodyRaw);

            request.downloadHandler =
                new DownloadHandlerBuffer();

            request.SetRequestHeader(
                "Content-Type",
                "application/json"
            );


            yield return request.SendWebRequest();


            if (request.result !=
                UnityWebRequest.Result.Success)
            {
                Debug.LogError(
                    $"❌ LLM Request 失敗：{request.error}"
                );

                yield break;
            }


            string responseJson =
                request.downloadHandler.text;

            Debug.Log(
                $"📥 Python 回傳：{responseJson}"
            );


            ChatResponse chatResponse =
                JsonUtility.FromJson<ChatResponse>(
                    responseJson
                );


            Debug.Log(
                $"🤖 大師回答：{chatResponse.reply}"
            );

            yield return StartCoroutine(
                RequestTTSAndPlay(chatResponse.reply)
            );
            isBusy = false;
        }
    }

    private IEnumerator RequestTTSAndPlay(string text)
    {
        TTSRequest ttsRequest =
            new TTSRequest
            {
                text = text
            };

        string json =
            JsonUtility.ToJson(ttsRequest);

        Debug.Log(
            $"🔊 傳送給 TTS：{json}"
        );


        using (UnityWebRequest request =
            UnityWebRequest.Post(
                ttsUrl,
                json,
                "application/json"
            ))
        {
            request.downloadHandler =
                new DownloadHandlerAudioClip(
                    ttsUrl,
                    AudioType.MPEG
                );


            yield return request.SendWebRequest();


            if (request.result !=
                UnityWebRequest.Result.Success)
            {
                Debug.LogError(
                    $"❌ TTS Request 失敗：{request.error}"
                );
                
                isBusy = false;

                yield break;
            }


            AudioClip clip = DownloadHandlerAudioClip.GetContent(request);

            if (clip == null)
            {
                Debug.LogError(
                    "❌ TTS MP3 無法轉換成 AudioClip"
                );

                yield break;
            }


            Task a2fTask = null;

            if (a2fController != null)
            {
                a2fTask =
                    a2fController.ProcessAudioClip(clip);
            }
            else
            {
                Debug.LogWarning(
                    "⚠️ ConversationManager 尚未設定 A2FController"
                );
            }


            ttsAudioSource.clip = clip;
            ttsAudioSource.Play();

            Debug.Log(
                $"▶️ 開始播放 TTS，長度：{clip.length:F2} 秒"
            );


            while (ttsAudioSource.isPlaying)
            {
                yield return null;
            }


            Debug.Log(
                "✅ TTS 播放完成"
            );

            if (a2fTask != null)
            {
                while (!a2fTask.IsCompleted)
                {
                    yield return null;
                }

                if (a2fTask.IsFaulted)
                {
                    Debug.LogError(
                        $"❌ A2F Task 發生錯誤：{a2fTask.Exception}"
                    );
                }
            }
        }
    }

    void OnDestroy()
    {
        if (sttReceiver != null)
        {
            sttReceiver.OnTranscriptReceived -=
                HandleTranscript;
        }
    }
}