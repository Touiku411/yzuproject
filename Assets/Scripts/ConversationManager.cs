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
        public string emotion;
    }

    [Serializable]
    private class TTSRequest
    {
        public string text;
    }

    [Header("STT")]
    public STTReceiver sttReceiver;

    [Header("LLM")]
    [SerializeField]
    private string llmUrl = "http://127.0.0.1:8000/chat";

    [SerializeField]
    private string clearHistoryUrl =
        "http://127.0.0.1:8000/clear-history";

    [Header("TTS")]
    [SerializeField]
    private string ttsUrl = "http://127.0.0.1:8000/tts";

    [SerializeField]
    private AudioSource ttsAudioSource;

    [Header("ZeroEGGS")]
    [SerializeField]
    private ZeroEggsRuntimePlayer zeroEggsPlayer;
    
    [Header("A2F")]
    [SerializeField]
    private A2FController a2fController;

    private STTReceiver subscribedReceiver;
    private AudioClip currentClip;
    private bool isBusy;
    private bool ownsPlayback;

    void OnEnable()
    {
        if (sttReceiver == null)
        {
            Debug.LogError(
                "ConversationManager 尚未設定 STTReceiver",
                this
            );
            return;
        }

        subscribedReceiver = sttReceiver;
        subscribedReceiver.OnTranscriptReceived += HandleTranscript;
    }

    private void HandleTranscript(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (isBusy)
        {
            Debug.Log($"[對話] 正在回答，暫時忽略：{text}", this);
            return;
        }

        if (zeroEggsPlayer == null ||
            !zeroEggsPlayer.isActiveAndEnabled ||
            ttsAudioSource == null ||
            !ttsAudioSource.isActiveAndEnabled)
        {
            Debug.LogError(
                "請設定並啟用 ZeroEggs Player 和 TTS Audio Source",
                this
            );
            return;
        }

        if (zeroEggsPlayer.IsPlaying)
        {
            Debug.LogWarning("ZeroEGGS 正在播放，請稍後再說話", this);
            return;
        }

        isBusy = true;
        StartCoroutine(GenerateAndPlay(text.Trim()));
    }

    private IEnumerator GenerateAndPlay(string userText)
    {
        // 無論成功或中途失敗，都釋放音訊並解除忙碌狀態。
        try
        {
            Debug.Log($"[對話] STT：{userText}", this);

            ChatResponse answer;

            string chatJson = JsonUtility.ToJson(
                new ChatRequest { text = userText }
            );

            using (UnityWebRequest request = CreateJsonPost(
                llmUrl, chatJson))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 130;

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError(
                        $"[對話] LLM 失敗：{request.error}\n" +
                        request.downloadHandler.text,
                        this
                    );
                    yield break;
                }

                answer = ParseChatResponse(request.downloadHandler.text);
            }

            if (answer == null ||
                string.IsNullOrWhiteSpace(answer.reply))
            {
                Debug.LogError("[對話] LLM 回覆缺少 reply", this);
                yield break;
            }

            string emotion = NormalizeEmotion(answer.emotion);

            Debug.Log($"[對話] 大師：{answer.reply}", this);
            Debug.Log($"[對話] 情緒：{emotion}", this);

            string ttsJson = JsonUtility.ToJson(
                new TTSRequest { text = answer.reply }
            );

            Debug.Log("[對話] 正在產生 TTS 語音……", this);

            using (UnityWebRequest request = CreateJsonPost(
                ttsUrl, ttsJson))
            {
                request.downloadHandler = new DownloadHandlerAudioClip(
                    ttsUrl,
                    AudioType.MPEG
                );
                request.timeout = 900;

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError(
                        $"[對話] TTS 失敗：{request.error}",
                        this
                    );
                    yield break;
                }

                currentClip = DownloadHandlerAudioClip.GetContent(request);

                if (currentClip == null || currentClip.length <= 0)
                {
                    Debug.LogError("[對話] 無法讀取 TTS 音訊", this);
                    yield break;
                }

                // 等待下載的 AudioClip 完成載入，供 Prepare 讀取 PCM。
                while (currentClip.loadState == AudioDataLoadState.Loading)
                    yield return null;

                if (currentClip.loadState != AudioDataLoadState.Loaded)
                {
                    Debug.LogError("[對話] TTS 音訊載入失敗", this);
                    yield break;
                }

                Debug.Log(
                    $"[對話] 語音完成：{currentClip.length:F2} 秒；" +
                    "開始生成動作……",
                    this
                );

                // 本階段沿用目前伺服器的風格設定。
                yield return zeroEggsPlayer.Prepare(currentClip, emotion);

                // 由 ZeroEGGS 統一安排語音與身體動作開始時間。
                ownsPlayback = zeroEggsPlayer.PlayPrepared(
                    currentClip,
                    ttsAudioSource
                );

                if (!ownsPlayback)
                {
                    Debug.LogError(
                        "[對話] 無法開始同步播放，請查看 ZeroEGGS 錯誤",
                        this
                    );
                    yield break;
                }

                // 將同一份 TTS 語音交給 A2F 產生臉部表情。
                Task a2fTask = null;

                if (a2fController != null && a2fController.isActiveAndEnabled)
                {
                    try
                    {
                        a2fTask = a2fController.ProcessAudioClip(currentClip);
                        Debug.Log("[對話] 已將語音送至 A2F", this);
                    }
                    catch (Exception error)
                    {
                        Debug.LogError(
                            $"[對話] A2F 啟動失敗：{error.Message}",
                            this
                        );
                    }
                }
                else
                {
                    Debug.LogWarning(
                        "[對話] A2F 未指定或未啟用",
                        this
                    );
                }

                Debug.Log("[對話] 語音與身體動作共同開始", this);

                while (zeroEggsPlayer.IsPlaying)
                    yield return null;

                // 等待 A2F 處理結束，避免下一輪表情與本輪重疊。
                if (a2fTask != null)
                {
                    while (!a2fTask.IsCompleted)
                        yield return null;

                    if (a2fTask.IsFaulted)
                    {
                        Debug.LogError(
                            $"[對話] A2F 處理失敗：{a2fTask.Exception}",
                            this
                        );
                    }
                    else if (a2fTask.IsCanceled)
                    {
                        Debug.LogWarning("[對話] A2F 處理已取消", this);
                    }
                }

                Debug.Log("[對話] 播放完成，可以繼續說話", this);
            }
        }
        finally
        {
            ReleaseAudio();
            isBusy = false;
        }
    }

    private static UnityWebRequest CreateJsonPost(
        string url,
        string json)
    {
        UnityWebRequest request = new UnityWebRequest(url, "POST");

        request.uploadHandler = new UploadHandlerRaw(
            Encoding.UTF8.GetBytes(json)
        );

        request.SetRequestHeader("Content-Type", "application/json");

        return request;
    }

    private ChatResponse ParseChatResponse(string json)
    {
        try
        {
            return JsonUtility.FromJson<ChatResponse>(json);
        }
        catch (Exception error)
        {
            Debug.LogError(
                $"[對話] LLM 回覆格式錯誤：{error.Message}\n{json}",
                this
            );
            return null;
        }
    }

    private static string NormalizeEmotion(string emotion)
    {
        string value = (emotion ?? "neutral").Trim().ToLowerInvariant();

        switch (value)
        {
            case "happy":
            case "sad":
            case "angry":
            case "relaxed":
                return value;

            default:
                return "neutral";
        }
    }

    public void ClearConversation()
    {
        if (isBusy)
        {
            Debug.LogWarning("請等待目前回答完成後再清除對話", this);
            return;
        }

        StartCoroutine(SendClearHistoryRequest());
    }

    private IEnumerator SendClearHistoryRequest()
    {
        isBusy = true;

        try
        {
            using (UnityWebRequest request =
                new UnityWebRequest(clearHistoryUrl, "POST"))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 15;

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError(
                        $"[對話] 清除紀錄失敗：{request.error}",
                        this
                    );
                    yield break;
                }

                Debug.Log("[對話] 對話紀錄已清除", this);
            }
        }
        finally
        {
            isBusy = false;
        }
    }

    private void ReleaseAudio()
    {
        if (ownsPlayback && zeroEggsPlayer != null)
            zeroEggsPlayer.StopPlayback();

        ownsPlayback = false;

        if (currentClip == null)
            return;

        if (ttsAudioSource != null &&
            ttsAudioSource.clip == currentClip)
        {
            ttsAudioSource.Stop();
            ttsAudioSource.clip = null;
        }

        Destroy(currentClip);
        currentClip = null;
    }

    void OnDisable()
    {
        if (subscribedReceiver != null)
        {
            subscribedReceiver.OnTranscriptReceived -= HandleTranscript;
            subscribedReceiver = null;
        }

        ReleaseAudio();
        StopAllCoroutines();
        isBusy = false;
    }
}