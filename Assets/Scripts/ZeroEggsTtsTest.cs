using System;
using System.Collections;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public class ZeroEggsTtsTest : MonoBehaviour
{
    [Serializable]
    private class TTSRequest
    {
        public string text;
    }

    [Header("ZeroEGGS")]
    public ZeroEggsRuntimePlayer player;

    [Header("A2F")]
    public A2FController a2fController;

    [Header("TTS")]
    public string ttsUrl = "http://127.0.0.1:8000/tts";

    [TextArea(3, 8)]
    public string testSpeech =
        "各位大家好，今天很高興能和大家一起討論這個問題。";

    private bool isBusy;
    private bool ownsPlayback;
    private AudioClip currentClip;
    private AudioSource currentOutput;

    [ContextMenu("測試 TTS 自動生成動作")]
    public void RunTest()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("請先進入 Play 模式", this);
            return;
        }

        if (isBusy)
        {
            Debug.LogWarning("測試正在執行，請等待完成", this);
            return;
        }

        if (player == null || !player.isActiveAndEnabled)
        {
            Debug.LogError("請指定並啟用 ZeroEggsRuntimePlayer", this);
            return;
        }

        if (player.audioOutput == null ||
            !player.audioOutput.isActiveAndEnabled)
        {
            Debug.LogError("請設定並啟用 Player 的 Audio Output", this);
            return;
        }

        if (player.IsPlaying)
        {
            Debug.LogWarning("ZeroEGGS 正在播放，請稍後再測試", this);
            return;
        }

        if (string.IsNullOrWhiteSpace(testSpeech))
        {
            Debug.LogError("請輸入測試文字", this);
            return;
        }

        isBusy = true;
        StartCoroutine(GenerateAndPlay());
    }

    private IEnumerator GenerateAndPlay()
    {
        try
        {
            currentOutput = player.audioOutput;

            string json = JsonUtility.ToJson(
                new TTSRequest { text = testSpeech.Trim() }
            );

            Debug.Log("[TTS測試] 開始產生語音……", this);

            using (UnityWebRequest request =
                new UnityWebRequest(ttsUrl, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(
                    Encoding.UTF8.GetBytes(json)
                );

                request.downloadHandler = new DownloadHandlerAudioClip(
                    ttsUrl,
                    AudioType.MPEG
                );

                request.SetRequestHeader(
                    "Content-Type",
                    "application/json"
                );

                request.timeout = 900;

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError(
                        $"[TTS測試] TTS 失敗：{request.error}",
                        this
                    );
                    yield break;
                }

                currentClip = DownloadHandlerAudioClip.GetContent(request);

                if (currentClip == null || currentClip.length <= 0)
                {
                    Debug.LogError("[TTS測試] 無法讀取 TTS 音訊", this);
                    yield break;
                }

                while (currentClip.loadState == AudioDataLoadState.Loading)
                    yield return null;

                if (currentClip.loadState != AudioDataLoadState.Loaded)
                {
                    Debug.LogError("[TTS測試] 音訊載入失敗", this);
                    yield break;
                }

                Debug.Log(
                    $"[TTS測試] 語音完成，長度 {currentClip.length:F2} 秒；" +
                    "開始生成動作……",
                    this
                );

                yield return player.Prepare(currentClip);

                ownsPlayback = player.PlayPrepared(
                    currentClip,
                    currentOutput
                );

                if (!ownsPlayback)
                {
                    Debug.LogError(
                        "[TTS測試] 無法開始播放，請查看 ZeroEGGS 錯誤",
                        this
                    );
                    yield break;
                }

                // 同一份語音交給 A2F 處理臉部表情。
                Task a2fTask = StartA2F(currentClip);

                Debug.Log("[TTS測試] 語音與身體動作共同開始", this);

                while (player.IsPlaying)
                    yield return null;

                // 等待 A2F 完成，再開放下一次測試。
                if (a2fTask != null)
                {
                    while (!a2fTask.IsCompleted)
                        yield return null;

                    if (a2fTask.IsFaulted)
                    {
                        Debug.LogError(
                            $"[TTS測試] A2F 處理失敗：{a2fTask.Exception}",
                            this
                        );
                    }
                    else if (a2fTask.IsCanceled)
                    {
                        Debug.LogWarning(
                            "[TTS測試] A2F 處理已取消",
                            this
                        );
                    }
                }

                Debug.Log("[TTS測試] 播放與 A2F 處理已完成", this);
            }
        }
        finally
        {
            ReleaseAudio();
            isBusy = false;
        }
    }

    private Task StartA2F(AudioClip clip)
    {
        if (a2fController == null ||
            !a2fController.isActiveAndEnabled)
        {
            Debug.LogWarning(
                "[TTS測試] A2F 未指定或未啟用，本次只播放語音與身體動作",
                this
            );
            return null;
        }

        try
        {
            Task task = a2fController.ProcessAudioClip(clip);
            Debug.Log("[TTS測試] 已將語音送至 A2F", this);
            return task;
        }
        catch (Exception error)
        {
            Debug.LogError(
                $"[TTS測試] A2F 啟動失敗：{error.Message}",
                this
            );
            return null;
        }
    }

    private void ReleaseAudio()
    {
        bool outputMatches = currentClip != null &&
            currentOutput != null &&
            currentOutput.clip == currentClip;

        if (ownsPlayback && player != null && outputMatches)
            player.StopPlayback();

        ownsPlayback = false;

        if (outputMatches)
        {
            currentOutput.Stop();
            currentOutput.clip = null;
        }

        if (currentClip != null)
            Destroy(currentClip);

        currentClip = null;
        currentOutput = null;
    }

    void OnDisable()
    {
        ReleaseAudio();
        StopAllCoroutines();
        isBusy = false;
    }
}