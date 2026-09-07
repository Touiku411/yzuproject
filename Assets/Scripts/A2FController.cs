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
    [Header("連線設定 (已被程式碼強制覆蓋)")]
    [Tooltip("為了避免格式錯誤，網址已在腳本內寫死為 http://127.0.0.1:52000")]
    public string serverAddress = "http://127.0.0.1:52000";

    [Header("身體動畫控制")]
    public Animator bodyAnimator;

    [Header("要連動的所有網格 (臉皮、牙齒、舌頭)")]
    [Tooltip("請把場景中的 head_lod0_ORIGINAL 拖曳到這裡")]
    public SkinnedMeshRenderer[] targetRenderers;

    [Tooltip("請放入一段大師說話的測試語音 (MP3/WAV)")]
    public AudioClip testAudioClip;


    [Header("Audio2Face 標準表情對應名稱")]
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

    void Start()
    {
        string targetAddress = "127.0.0.1:52000"; 
        channel = new Channel(targetAddress, ChannelCredentials.Insecure);
        client = new A2FControllerService.A2FControllerServiceClient(channel);
        Debug.Log("✅ gRPC 原生核心連線成功！");
    }
    // 💡 這裡改成了公開方法，讓 UI 按鈕可以直接呼叫，避開鍵盤系統衝突
    public void TriggerA2FTest()
    {
        if (testAudioClip != null)
        {
            Debug.Log("🚀 開始傳送語音並接收動畫...");
            AudioSource.PlayClipAtPoint(testAudioClip, transform.position);

            if (bodyAnimator != null)
            {
                bodyAnimator.SetBool("IsTalking", true);
            }
            _ = SendAudioAndReceiveAnimation(testAudioClip);
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

    //private void UpdateBlendShapes(float[] arkitWeights)
    //{
    //    // 1. 如果陣列是空的，直接跳出
    //    if (targetRenderers == null || targetRenderers.Length == 0) return;

    //    // 2. 用 foreach 迴圈，把數據倒給陣列裡的所有網格 (臉皮、牙齒、舌頭)
    //    foreach (var renderer in targetRenderers)
    //    {
    //        // 防呆：如果某個格子空著，或是網格壞了，就跳過它
    //        if (renderer == null || renderer.sharedMesh == null) continue;

    //        int maxIndex = Mathf.Min(renderer.sharedMesh.blendShapeCount, arkitWeights.Length);
    //        for (int i = 0; i < maxIndex; i++)
    //        {
    //            renderer.SetBlendShapeWeight(i, arkitWeights[i] * 100f);
    //        }
    //    }
    //}
    private void UpdateBlendShapes(float[] arkitWeights)
    {
        if (targetRenderers == null || targetRenderers.Length == 0) return;

        foreach (var renderer in targetRenderers)
        {
            if (renderer == null || renderer.sharedMesh == null) continue;

            // 走訪 A2F 傳來的 52 個數值
            for (int i = 0; i < Mathf.Min(arkitWeights.Length, a2fBlendshapeNames.Length); i++)
            {
                // 1. 取得標準表情名稱
                string bsName = a2fBlendshapeNames[i];

                // 2. 用名字去模型身上尋找正確的編號 (Index)
                int meshIndex = renderer.sharedMesh.GetBlendShapeIndex(bsName);

                // 防呆機制：HANA Tool 產生的名字字首可能是小寫 (例如 eyeBlinkLeft)
                if (meshIndex == -1)
                {
                    string lowerName = char.ToLower(bsName[0]) + bsName.Substring(1);
                    meshIndex = renderer.sharedMesh.GetBlendShapeIndex(lowerName);
                }

                // 3. 如果模型身上真的有這個表情，才把數值套用上去
                if (meshIndex != -1)
                {
                    renderer.SetBlendShapeWeight(meshIndex, arkitWeights[i] * 100f);
                }
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