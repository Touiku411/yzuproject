using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Grpc.Core;

using NvidiaAce.Services.A2FController.V1; 
using NvidiaAce.Controller.V1;
using NvidiaAce.Audio.V1;
using NvidiaAce.A2F.V1;           
using NvidiaAce.AnimationData.V1;
using A2FStream = NvidiaAce.Controller.V1.AudioStream;
using A2FResponse = NvidiaAce.Controller.V1.AnimationDataStream;
using A2FCall = Grpc.Core.AsyncDuplexStreamingCall<NvidiaAce.Controller.V1.AudioStream, NvidiaAce.Controller.V1.AnimationDataStream>;
using A2FStatusCode = NvidiaAce.Status.V1.Status.Types.Code;

public class A2FController : MonoBehaviour
{
    [Header("A2F gRPC 連線設定")]
    [Tooltip("A2F gRPC 位址，不需要加 http://")]
    public string serverAddress = "127.0.0.1:52000";
    // public string serverAddress = "10.42.10.76:52000";
    

    [Header("身體動畫控制")]
    public Animator bodyAnimator;

    [Header("要連動的所有網格 (臉皮、牙齒、舌頭)")]
    [Tooltip("請把場景中的 head_lod0_ORIGINAL 拖曳到這裡")]
    public SkinnedMeshRenderer[] targetRenderers;

    [Tooltip("請放入一段大師說話的測試語音 (MP3/WAV)")]
    public AudioClip testAudioClip;

    [Header("長語音串流")]
    [Range(0.05f, 1f)]
    public float audioChunkSeconds = 0.5f;
    [Min(30f)]
    public float requestTimeoutSeconds = 180f;
    [Tooltip("可留空：自動尋找正在播放同一份 AudioClip 的 AudioSource。")]
    public AudioSource playbackAudioSource;


    [Header("Audio2Face 標準表情對應名稱")]
    // 僅供開發階段檢查 Mesh 的 BlendShape Index 順序。
    private readonly string[] a2fBlendshapeNames = new string[]
    {
        "EyeBlinkLeft", "EyeLookDownLeft", "EyeLookInLeft", "EyeLookOutLeft", "EyeLookUpLeft",
        "EyeSquintLeft", "EyeWideLeft", "EyeBlinkRight", "EyeLookDownRight", "EyeLookInRight",
        "EyeLookOutRight", "EyeLookUpRight", "EyeSquintRight", "EyeWideRight", "JawForward",
        "JawLeft", "JawRight", "JawOpen", "MouthClose", "MouthFunnel", "MouthPucker",
        "MouthLeft", "MouthRight", "MouthSmileLeft", "MouthSmileRight", "MouthFrownLeft",
        "MouthFrownRight", "MouthDimpleLeft", "MouthDimpleRight", "MouthStretchLeft",
        "MouthStretchRight", "MouthRollLower", "MouthRollUpper", "MouthShrugLower",
        "MouthShrugUpper", "MouthPressLeft", "MouthPressRight", "MouthLowerDownLeft",
        "MouthLowerDownRight", "MouthUpperUpLeft", "MouthUpperUpRight", "BrowDownLeft",
        "BrowDownRight", "BrowInnerUp", "BrowOuterUpLeft", "BrowOuterUpRight", "CheekPuff",
        "CheekSquintLeft", "CheekSquintRight", "NoseSneerLeft", "NoseSneerRight", "TongueOut"
    };

    private Channel channel; // 注意：這裡用的是 Grpc.Core 的 Channel
    private A2FControllerService.A2FControllerServiceClient client;

    private sealed class FaceFrame
    {
        public double TimeCode;
        public float[] Weights;
    }

    private sealed class PlaybackSession
    {
        public readonly ConcurrentQueue<FaceFrame> Frames = new ConcurrentQueue<FaceFrame>();
        public readonly TaskCompletionSource<bool> PlaybackFinished =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancellation;
        public Exception IoFailure;
        public AudioClip Clip;
        public AudioSource Source;
        public double StartDspTime;
        public double Elapsed;
        public double LastFrameTime;
        public FaceFrame CurrentFrame;
        public float[] BlendedWeights;
        public volatile bool NetworkFinished;
        public int ReceivedFrames;
        public int AppliedUpdates;
    }

    private PlaybackSession activeSession;
    private AudioSource testAudioSource;
    public bool IsProcessing { get { return activeSession != null; } }
    public int LastReceivedFrameCount { get; private set; }


    async void Start()
    {
        channel = new Channel(
            serverAddress,
            ChannelCredentials.Insecure
        );

        client =
            new A2FControllerService
                .A2FControllerServiceClient(channel);

        Debug.Log(
            $" 正在連線 A2F：{serverAddress}"
        );

        try
        {
            await channel.ConnectAsync(
                DateTime.UtcNow.AddSeconds(5)
            );

            Debug.Log(
                $" A2F gRPC 連線成功：{serverAddress}"
            );
        }
        catch (Exception e)
        {
            Debug.LogError(
                $" A2F gRPC 無法連線：{serverAddress}\n{e.Message}"
            );
        }
    }

    // 保留原有方法簽章，ConversationManager／ZeroEggsTtsTest 不必改呼叫方式。
    public Task ProcessAudioClip(AudioClip clip)
    {
        if (clip == null)
            return Task.FromException(new ArgumentNullException(nameof(clip)));
        if (!isActiveAndEnabled)
            return Task.FromException(new InvalidOperationException("A2FController 尚未啟用"));
        if (client == null)
            return Task.FromException(new InvalidOperationException("A2F gRPC Client 尚未初始化"));
        if (IsProcessing)
            return Task.FromException(new InvalidOperationException("A2F 正在處理上一段音訊"));

        return SendAudioAndReceiveAnimation(clip);
    }

    public async void TriggerA2FTest()
    {
        if (!Application.isPlaying || testAudioClip == null || IsProcessing)
        {
            Debug.LogWarning("請先進入 Play、指定 Test Audio Clip，並等待上一輪完成。", this);
            return;
        }

        // 測試使用自己的 AudioSource，避免 PlayClipAtPoint 產生無法追蹤的播放來源。
        if (testAudioSource == null)
        {
            testAudioSource = gameObject.AddComponent<AudioSource>();
            testAudioSource.playOnAwake = false;
            testAudioSource.spatialBlend = 0f;
        }
        testAudioSource.Stop();
        testAudioSource.clip = testAudioClip;
        testAudioSource.loop = false;
        testAudioSource.pitch = 1f;
        testAudioSource.PlayScheduled(AudioSettings.dspTime + 0.1);

        try { await ProcessAudioClip(testAudioClip); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Debug.LogError("[A2F測試] " + error.Message, this); }
        finally
        {
            if (testAudioSource != null)
                testAudioSource.Stop();
        }
    }

    private async Task SendAudioAndReceiveAnimation(AudioClip clip)
    {
        var session = new PlaybackSession
        {
            Clip = clip,
            Source = FindPlaybackSource(clip),
            StartDspTime = AudioSettings.dspTime
        };
        activeSession = session;
        LastReceivedFrameCount = 0;

        using (var cancellation = new CancellationTokenSource())
        {
            session.Cancellation = cancellation;
            double timeout = Math.Max(Math.Max(30.0, requestTimeoutSeconds), clip.length * 3.0 + 30.0);
            cancellation.CancelAfter(TimeSpan.FromSeconds(timeout));

            using (cancellation.Token.Register(() => session.PlaybackFinished.TrySetCanceled()))
            {
                try
                {
                    if (bodyAnimator != null)
                    {
                        bodyAnimator.SetInteger("TalkVariant", UnityEngine.Random.Range(0, 3));
                        bodyAnimator.SetBool("IsTalking", true);
                    }

                    byte[] pcmData = ConvertAudioClipToPCM(clip);
                    Debug.Log($"[A2F] 長度={clip.length:F2} 秒，取樣率={clip.frequency} Hz，" +
                        $"輸入聲道={clip.channels}，送出聲道=1，PCM={pcmData.Length / 1048576f:F2} MiB", this);

                    using (var call = client.ProcessAudioStream(
                        deadline: DateTime.UtcNow.AddSeconds(timeout),
                        cancellationToken: cancellation.Token))
                    {
                        // 先開始讀回傳，再開始寫入；兩個方向各只有一個讀／寫工作。
                        // 不使用 Task.Run，Unity 元件的操作仍由主執行緒處理。
                        Task receiveTask = GuardIoAsync(
                            () => ReceiveAnimationAsync(call, session), session);
                        Task sendTask = GuardIoAsync(
                            () => SendChunksAsync(call, pcmData, clip.frequency, session), session);

                        try { await Task.WhenAll(receiveTask, sendTask); }
                        catch
                        {
                            // 保留真正的服務錯誤，不讓另一方向的 Cancelled 蓋掉原因。
                            if (session.IoFailure != null)
                                ExceptionDispatchInfo.Capture(session.IoFailure).Throw();
                            throw;
                        }
                    }

                    if (session.ReceivedFrames == 0)
                        throw new InvalidOperationException("A2F 串流結束，但沒有任何嘴型幀");

                    session.NetworkFinished = true;
                    Debug.Log($"[A2F] 接收完成：{session.ReceivedFrames} 幀；等待嘴型播放完成。", this);
                    await session.PlaybackFinished.Task;
                    Debug.Log($"[A2F] 嘴型播放完成，接收 {session.ReceivedFrames} 幀。", this);
                }
                catch (RpcException error)
                {
                    if (cancellation.IsCancellationRequested && session.IoFailure == null)
                        throw new OperationCanceledException("A2F 作業已取消", error, cancellation.Token);
                    Debug.LogError($"[A2F] gRPC {error.StatusCode}：{error.Status.Detail}", this);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    Debug.LogWarning("[A2F] 作業已取消或超過時限。", this);
                    throw;
                }
                catch (Exception error)
                {
                    Debug.LogError("[A2F] 處理失敗：" + error.Message, this);
                    throw;
                }
                finally
                {
                    cancellation.Cancel();
                    LastReceivedFrameCount = session.ReceivedFrames;
                    if (ReferenceEquals(activeSession, session))
                    {
                        activeSession = null;
                        ResetFaceBlendShapes();
                        if (bodyAnimator != null)
                            bodyAnimator.SetBool("IsTalking", false);
                    }
                }
            }
        }
    }

    private static async Task GuardIoAsync(Func<Task> operation, PlaybackSession session)
    {
        try { await operation(); }
        catch (Exception error)
        {
            if (!session.Cancellation.IsCancellationRequested)
            {
                Interlocked.CompareExchange(ref session.IoFailure, error, null);
                session.Cancellation.Cancel();
            }
            throw;
        }
    }

    private async Task SendChunksAsync(A2FCall call, byte[] pcmData, int sampleRate, PlaybackSession session)
    {
        // Header 必須是第一包；此轉換函式輸出的格式固定為單聲道 PCM16。
        await call.RequestStream.WriteAsync(new A2FStream
        {
            AudioStreamHeader = new NvidiaAce.Controller.V1.AudioStreamHeader
            {
                AudioHeader = new AudioHeader
                {
                    AudioFormat = AudioHeader.Types.AudioFormat.Pcm,
                    ChannelCount = 1,
                    SamplesPerSecond = (uint)sampleRate,
                    BitsPerSample = 16
                }
            }
        });
        Debug.Log("[A2F] Header 已送出。", this);

        int samplesPerChunk = Math.Max(1, (int)Math.Round(sampleRate * Mathf.Clamp(audioChunkSeconds, 0.05f, 1f)));
        int bytesPerChunk = checked(samplesPerChunk * 2);
        int count = 0;
        for (int offset = 0; offset < pcmData.Length; offset += bytesPerChunk)
        {
            session.Cancellation.Token.ThrowIfCancellationRequested();
            int length = Math.Min(bytesPerChunk, pcmData.Length - offset);
            await call.RequestStream.WriteAsync(new A2FStream
            {
                AudioWithEmotion = new AudioWithEmotion
                {
                    AudioBuffer = Google.Protobuf.ByteString.CopyFrom(pcmData, offset, length)
                }
            });
            count++;
        }

        // 只送分包，不能在這裡再補送整段 pcmData。
        await call.RequestStream.WriteAsync(new A2FStream
        {
            EndOfAudio = new A2FStream.Types.EndOfAudio()
        });
        await call.RequestStream.CompleteAsync();
        Debug.Log($"[A2F] 音訊已分成 {count} 包送出，EndOfAudio 已送出。", this);
    }

    private async Task ReceiveAnimationAsync(A2FCall call, PlaybackSession session)
    {
        double previousTimeCode = -1;
        while (await call.ResponseStream.MoveNext(session.Cancellation.Token))
        {
            var response = call.ResponseStream.Current;
            if (response.Status != null)
            {
                var status = response.Status;
                Debug.Log($"[A2F服務狀態] {status.Code}：{status.Message}", this);
                if (status.Code == A2FStatusCode.Error)
                    throw new InvalidOperationException("A2F 服務拒絕處理：" + status.Message);
            }

            var frames = response.AnimationData?.SkelAnimation?.BlendShapeWeights;
            if (frames == null) continue;

            // 每批中的所有幀都要保留，而不是只取 BlendShapeWeights[0]。
            foreach (var frame in frames)
            {
                if (frame.Values.Count == 0) continue;
                if (double.IsNaN(frame.TimeCode) || double.IsInfinity(frame.TimeCode) ||
                    frame.TimeCode < 0 || frame.TimeCode < previousTimeCode)
                    throw new InvalidOperationException("A2F 回傳的表情時間碼不正確");

                var weights = new float[frame.Values.Count];
                for (int i = 0; i < weights.Length; i++)
                {
                    float value = frame.Values[i];
                    if (float.IsNaN(value) || float.IsInfinity(value))
                        throw new InvalidOperationException("A2F 回傳的表情含 NaN／Infinity");
                    weights[i] = value;
                }
                previousTimeCode = frame.TimeCode;
                session.LastFrameTime = frame.TimeCode;
                session.Frames.Enqueue(new FaceFrame { TimeCode = frame.TimeCode, Weights = weights });
                session.ReceivedFrames++;
            }
        }
        // 這裡只接收與緩衝，不放 Task.Delay，也不在這裡更新臉部網格。
    }

    private void Update()
    {
        var session = activeSession;
        if (session == null || session.Cancellation == null || session.Cancellation.IsCancellationRequested)
            return;

        double elapsed = Math.Max(0, AudioSettings.dspTime - session.StartDspTime);
        if (session.Source != null && session.Source.clip == session.Clip && session.Source.isPlaying)
            elapsed = session.Source.timeSamples / (double)session.Clip.frequency;
        session.Elapsed = Math.Max(session.Elapsed, elapsed);

        FaceFrame next;
        while (session.Frames.TryPeek(out next) && next.TimeCode <= session.Elapsed)
        {
            session.Frames.TryDequeue(out next);
            session.CurrentFrame = next;
        }

        var current = session.CurrentFrame;
        if (current != null)
        {
            if (session.Frames.TryPeek(out next) && next.TimeCode > current.TimeCode &&
                next.Weights.Length == current.Weights.Length)
            {
                if (session.BlendedWeights == null || session.BlendedWeights.Length != current.Weights.Length)
                    session.BlendedWeights = new float[current.Weights.Length];
                float blend = Mathf.Clamp01((float)((session.Elapsed - current.TimeCode) /
                    (next.TimeCode - current.TimeCode)));
                for (int i = 0; i < session.BlendedWeights.Length; i++)
                    session.BlendedWeights[i] = Mathf.Lerp(current.Weights[i], next.Weights[i], blend);
                UpdateBlendShapes(session.BlendedWeights);
            }
            else
                UpdateBlendShapes(current.Weights);
            session.AppliedUpdates++;
        }

        if (session.NetworkFinished && session.Frames.IsEmpty &&
            session.Elapsed >= Math.Max(session.Clip.length, session.LastFrameTime))
            session.PlaybackFinished.TrySetResult(true);
    }

    private AudioSource FindPlaybackSource(AudioClip clip)
    {
        if (playbackAudioSource != null && playbackAudioSource.clip == clip && playbackAudioSource.isActiveAndEnabled)
            return playbackAudioSource;
        // 相容目前 ConversationManager：它在呼叫 A2F 前已將 clip 指定給 AudioSource。
        foreach (var source in UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
            if (source.isActiveAndEnabled && source.clip == clip)
                return source;
        return null;
    }

    private byte[] ConvertAudioClipToPCM(AudioClip clip)
    {
        if (clip.samples <= 0 || clip.channels <= 0 || clip.frequency <= 0)
            throw new InvalidOperationException("AudioClip 沒有有效的音訊資料");
        float[] samples = new float[checked(clip.samples * clip.channels)];
        if (!clip.GetData(samples, 0))
            throw new InvalidOperationException("AudioClip.GetData 失敗，請確認音訊已載入且可讀取 PCM");

        byte[] pcm = new byte[checked(clip.samples * 2)];
        for (int i = 0; i < clip.samples; i++)
        {
            float mono = 0;
            for (int channelIndex = 0; channelIndex < clip.channels; channelIndex++)
                mono += samples[i * clip.channels + channelIndex] / clip.channels;
            if (float.IsNaN(mono) || float.IsInfinity(mono)) mono = 0;
            short value = (short)Mathf.RoundToInt(Mathf.Clamp(mono, -1f, 1f) * 32767f);
            pcm[i * 2] = (byte)(value & 0xff);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xff);
        }
        return pcm;
    }

    private void ResetFaceBlendShapes()
    {
        UpdateBlendShapes(new float[a2fBlendshapeNames.Length]);
    }

    [ContextMenu("檢查 A2F 52-Key Index")]
    private void ValidateA2FBlendShapeIndex()
    {
        if (targetRenderers == null || targetRenderers.Length == 0)
        {
            Debug.LogWarning(" 請先設定 targetRenderers", this);
            return;
        }

        foreach (var renderer in targetRenderers)
        {
            if (renderer == null)
            {
                Debug.LogWarning(" targetRenderers 中有未指定的 Renderer", this);
                continue;
            }

            Debug.Log($"===== 檢查 {renderer.name} =====", renderer);

            if (renderer.sharedMesh == null)
            {
                Debug.LogWarning($" {renderer.name} 沒有 sharedMesh", renderer);
                continue;
            }

            int blendShapeCount = renderer.sharedMesh.blendShapeCount;
            Debug.Log($"BlendShape 總數：{blendShapeCount}", renderer);

            if (blendShapeCount < 52)
            {
                Debug.LogWarning($" {renderer.name} 只有 {blendShapeCount} 個 BlendShape，不到 52 個", renderer);
            }

            int count = Mathf.Min(52, blendShapeCount);
            int mismatchCount = 0;

            for (int i = 0; i < count; i++)
            {
                string meshName = renderer.sharedMesh.GetBlendShapeName(i);
                string a2fName = a2fBlendshapeNames[i];

                if (string.Equals(meshName, a2fName, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log($" [{i}] {a2fName}", renderer);
                }
                else
                {
                    mismatchCount++;
                    Debug.LogWarning($" [{i}] 不一致！ A2F = {a2fName}, Mesh = {meshName}", renderer);
                }
            }

            if (mismatchCount > 0)
            {
                Debug.LogWarning($" {renderer.name} 發現 {mismatchCount} 個 Index 不一致", renderer);
            }
            else if (count == 52)
            {
                Debug.Log($" {renderer.name} 的 A2F 52-Key Index 完全一致！", renderer);
            }
        }
    }

    private void UpdateBlendShapes(float[] arkitWeights)
    {
        if (targetRenderers == null || targetRenderers.Length == 0)
            return;

        foreach (var renderer in targetRenderers)
        {
            if (renderer == null || renderer.sharedMesh == null)
                continue;

            int count = Mathf.Min(
                52,
                arkitWeights.Length,
                renderer.sharedMesh.blendShapeCount
            );

            for (int i = 0; i < count; i++)
            {
                renderer.SetBlendShapeWeight(i, arkitWeights[i] * 100f);
            }
        }
    }

    private void OnDisable()
    {
        activeSession?.Cancellation?.Cancel();
        if (testAudioSource != null) testAudioSource.Stop();
        ResetFaceBlendShapes();
        if (bodyAnimator != null) bodyAnimator.SetBool("IsTalking", false);
    }

    private async void OnDestroy()
    {
        activeSession?.Cancellation?.Cancel();
        if (channel == null) return;
        try { await channel.ShutdownAsync(); }
        catch (Exception error) { Debug.LogWarning("[A2F] 關閉通道：" + error.Message); }
        finally { channel = null; client = null; }
    }
}
