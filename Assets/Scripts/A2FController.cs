using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Grpc.Net.Client;
using Grpc.Core;

using NvidiaAce.Services.A2FController.V1; 
using NvidiaAce.Controller.V1;
using NvidiaAce.Audio.V1;
using NvidiaAce.A2F.V1;           
using NvidiaAce.AnimationData.V1; 

public class A2FController : MonoBehaviour
{
    [Header("A2F gRPC 連線設定")]
    [Tooltip("A2F gRPC 位址，不需要加 http://")]
    public string serverAddress = "127.0.0.1:52000";

    [Header("身體動畫控制")]
    public Animator bodyAnimator;

    [Header("要連動的所有網格 (臉皮、牙齒、舌頭)")]
    [Tooltip("請把場景中的 head_lod0_ORIGINAL 拖曳到這裡")]
    public SkinnedMeshRenderer[] targetRenderers;

    [Tooltip("請放入一段大師說話的測試語音 (MP3/WAV)")]
    public AudioClip testAudioClip;


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
            $"🔌 正在連線 A2F：{serverAddress}"
        );

        try
        {
            await channel.ConnectAsync(
                DateTime.UtcNow.AddSeconds(5)
            );

            Debug.Log(
                $"✅ A2F gRPC 連線成功：{serverAddress}"
            );
        }
        catch (Exception e)
        {
            Debug.LogError(
                $"❌ A2F gRPC 無法連線：{serverAddress}\n{e.Message}"
            );
        }
    }

    public Task ProcessAudioClip(AudioClip clip)
    {
        if (clip == null)
        {
            Debug.LogWarning("⚠️ A2F 收到的 AudioClip 是空的！");
            return Task.CompletedTask;
        }

        if (client == null)
        {
            Debug.LogError("❌ A2F gRPC Client 尚未初始化！");
            return Task.CompletedTask;
        }

        if (bodyAnimator != null)
        {
            bodyAnimator.SetBool("IsTalking", true);
        }

        Debug.Log(
            $"🎭 A2F 開始處理 TTS AudioClip，長度：{clip.length:F2} 秒"
        );

        return SendAudioAndReceiveAnimation(clip);
    }


    // 💡 這裡改成了公開方法，讓 UI 按鈕可以直接呼叫，避開鍵盤系統衝突
    public void TriggerA2FTest()
    {
        if (testAudioClip != null)
        {
            Debug.Log("🚀 開始傳送語音並接收動畫...");

            AudioSource.PlayClipAtPoint(
                testAudioClip,
                transform.position
            );

            _ = ProcessAudioClip(testAudioClip);
        }
        else
        {
            Debug.LogWarning("⚠️ 請先在 Inspector 欄位中放入 Test Audio Clip 語音檔！");
        }
    }

    private async Task SendAudioAndReceiveAnimation(AudioClip clip)
    {
        try
        {
            byte[] pcmData = ConvertAudioClipToPCM(clip);
            using var call = client.ProcessAudioStream();

            // 1. 先送 Header (強制指定 Controller 版本的 AudioStream)
            await call.RequestStream.WriteAsync(new NvidiaAce.Controller.V1.AudioStream {
                AudioStreamHeader = new NvidiaAce.Controller.V1.AudioStreamHeader {
                    AudioHeader = new AudioHeader {
                        AudioFormat = AudioHeader.Types.AudioFormat.Pcm,
                        ChannelCount = (uint)clip.channels,
                        SamplesPerSecond = (uint)clip.frequency,
                        BitsPerSample = 16
                    }
                }
            });

            // 2. 送出語音本體 (外層用 Controller 版本，內層 Emotion 則用 A2F 版本)
            await call.RequestStream.WriteAsync(new NvidiaAce.Controller.V1.AudioStream {
                AudioWithEmotion = new NvidiaAce.A2F.V1.AudioWithEmotion { 
                    AudioBuffer = Google.Protobuf.ByteString.CopyFrom(pcmData)
                }
            });

            // 3. 告訴伺服器「我傳完了」
            await call.RequestStream.WriteAsync(new NvidiaAce.Controller.V1.AudioStream {
                EndOfAudio = new NvidiaAce.Controller.V1.AudioStream.Types.EndOfAudio()
            });
            await call.RequestStream.CompleteAsync();


            // 4. 一直接收伺服器算好的表情
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
                var response = call.ResponseStream.Current;
                
                // 強制指定使用 Controller 版本的 AnimationDataStream
                if (response.StreamPartCase == NvidiaAce.Controller.V1.AnimationDataStream.StreamPartOneofCase.AnimationData)
                {
                    var skelAnim = response.AnimationData.SkelAnimation;
                    if (skelAnim != null && skelAnim.BlendShapeWeights != null && skelAnim.BlendShapeWeights.Count > 0)
                    {
                        var currentFrame = skelAnim.BlendShapeWeights[0];
                        
                        if (currentFrame.Values != null && currentFrame.Values.Count > 0)
                        {
                            float[] weights = new float[currentFrame.Values.Count];
                            for(int i = 0; i < weights.Length; i++)
                            {
                                weights[i] = currentFrame.Values[i]; 
                            }
                            
                            UpdateBlendShapes(weights);
                            await Task.Delay(33); 
                        }
                    }
                }
            }
            Debug.Log("🎉 大師表情動畫播放完畢！");
        }
        catch (RpcException e)
        {
            Debug.LogError($"❌ gRPC 連線錯誤: {e.Status.Detail}");
        }
        finally
        {
            if (bodyAnimator != null)
            {
                bodyAnimator.SetBool("IsTalking", false);
            }
        }
    }

    private byte[] ConvertAudioClipToPCM(AudioClip clip)
    {
        float[] samples = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);

        byte[] pcmData = new byte[samples.Length * 2];
        int offset = 0;

        foreach (float sample in samples)
        {
            short intSample = (short)(Mathf.Clamp(sample, -1f, 1f) * 32767);
            byte[] byteArr = BitConverter.GetBytes(intSample);
            pcmData[offset] = byteArr[0];
            pcmData[offset + 1] = byteArr[1];
            offset += 2;
        }
        return pcmData;
    }

    [ContextMenu("檢查 A2F 52-Key Index")]
    private void ValidateA2FBlendShapeIndex()
    {
        if (targetRenderers == null || targetRenderers.Length == 0)
        {
            Debug.LogWarning("⚠️ 請先設定 targetRenderers", this);
            return;
        }

        foreach (var renderer in targetRenderers)
        {
            if (renderer == null)
            {
                Debug.LogWarning("⚠️ targetRenderers 中有未指定的 Renderer", this);
                continue;
            }

            Debug.Log($"===== 檢查 {renderer.name} =====", renderer);

            if (renderer.sharedMesh == null)
            {
                Debug.LogWarning($"⚠️ {renderer.name} 沒有 sharedMesh", renderer);
                continue;
            }

            int blendShapeCount = renderer.sharedMesh.blendShapeCount;
            Debug.Log($"BlendShape 總數：{blendShapeCount}", renderer);

            if (blendShapeCount < 52)
            {
                Debug.LogWarning($"⚠️ {renderer.name} 只有 {blendShapeCount} 個 BlendShape，不到 52 個", renderer);
            }

            int count = Mathf.Min(52, blendShapeCount);
            int mismatchCount = 0;

            for (int i = 0; i < count; i++)
            {
                string meshName = renderer.sharedMesh.GetBlendShapeName(i);
                string a2fName = a2fBlendshapeNames[i];

                if (string.Equals(meshName, a2fName, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log($"✅ [{i}] {a2fName}", renderer);
                }
                else
                {
                    mismatchCount++;
                    Debug.LogWarning($"❌ [{i}] 不一致！ A2F = {a2fName}, Mesh = {meshName}", renderer);
                }
            }

            if (mismatchCount > 0)
            {
                Debug.LogWarning($"⚠️ {renderer.name} 發現 {mismatchCount} 個 Index 不一致", renderer);
            }
            else if (count == 52)
            {
                Debug.Log($"🎉 {renderer.name} 的 A2F 52-Key Index 完全一致！", renderer);
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

    void OnDestroy()
    {
        if (channel != null)
        {
            // 原生通道需要用非同步的方式安全關閉
            channel.ShutdownAsync().Wait();
        }
    }
}
