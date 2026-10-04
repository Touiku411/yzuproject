using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Receives EchoAvatar motion on localhost:12346 and applies a first-pass
/// Humanoid bone mapping to the project's character. Audio is played by the
/// existing ConversationManager, not by this component.
/// </summary>
public sealed class EchoAvatarHumanoidReceiver : MonoBehaviour
{
    [Header("Character")]
    [SerializeField] private Animator characterAnimator;
    [SerializeField] private SkinnedMeshRenderer faceRenderer;
    [SerializeField] private bool applyFaceBlendshapes = false;
    [SerializeField] private bool applyRootTranslation = false;
    [Range(0f, 1f)] [SerializeField] private float rotationStrength = 1f;

    [Header("Motion stream")]
    [SerializeField] private int port = 12346;
    [Range(0, 30)] [SerializeField] private int delayFrames = 10;
    [SerializeField] private int maxBufferedFrames = 180;

    private const int SourceBoneCount = 88;
    private const int FrameRate = 60;
    private const int MaxPacketBytes = 8 * 1024 * 1024;

    // Indices are taken from EchoAvatar's bvhBoneNames in
    // StreamingServerFaceBodyAutoStartPushWav.cs. The source has additional
    // twist, heel, eye and finger joints without one-to-one Humanoid targets.
    private static readonly BoneDefinition[] BoneDefinitions =
    {
        new BoneDefinition(0, HumanBodyBones.Hips),
        new BoneDefinition(1, HumanBodyBones.RightUpperLeg),
        new BoneDefinition(3, HumanBodyBones.RightLowerLeg),
        new BoneDefinition(5, HumanBodyBones.RightFoot),
        new BoneDefinition(6, HumanBodyBones.RightToes),
        new BoneDefinition(10, HumanBodyBones.Spine),
        new BoneDefinition(12, HumanBodyBones.Chest),
        new BoneDefinition(13, HumanBodyBones.RightShoulder),
        new BoneDefinition(14, HumanBodyBones.RightUpperArm),
        new BoneDefinition(16, HumanBodyBones.RightLowerArm),
        new BoneDefinition(18, HumanBodyBones.RightHand),
        new BoneDefinition(40, HumanBodyBones.Neck),
        new BoneDefinition(42, HumanBodyBones.Head),
        new BoneDefinition(52, HumanBodyBones.LeftShoulder),
        new BoneDefinition(53, HumanBodyBones.LeftUpperArm),
        new BoneDefinition(55, HumanBodyBones.LeftLowerArm),
        new BoneDefinition(57, HumanBodyBones.LeftHand),
        new BoneDefinition(79, HumanBodyBones.LeftUpperLeg),
        new BoneDefinition(81, HumanBodyBones.LeftLowerLeg),
        new BoneDefinition(83, HumanBodyBones.LeftFoot),
        new BoneDefinition(84, HumanBodyBones.LeftToes)
    };

    private struct BoneDefinition
    {
        public readonly int SourceIndex;
        public readonly HumanBodyBones HumanBone;

        public BoneDefinition(int sourceIndex, HumanBodyBones humanBone)
        {
            SourceIndex = sourceIndex;
            HumanBone = humanBone;
        }
    }

    private struct BoneBinding
    {
        public int SourceIndex;
        public Transform Target;
        public Quaternion InitialLocalRotation;
    }

    private sealed class MotionPacket
    {
        public float[][][] pose;
        public float[][] trans;
        public float[][] blendshape;
        // The sender also includes audio. Json.NET ignores this unused property.
    }

    private struct MotionFrame
    {
        public float[][] Pose;
        public float[] Translation;
        public float[] Blendshape;
    }

    private readonly ConcurrentQueue<MotionPacket> receivedPackets = new ConcurrentQueue<MotionPacket>();
    private readonly Queue<MotionFrame> playback = new Queue<MotionFrame>();
    private readonly List<BoneBinding> bindings = new List<BoneBinding>();
    private Thread networkThread;
    private TcpListener listener;
    private TcpClient client;
    private volatile bool running;
    private float frameTimer;
    private Transform hips;
    private Vector3 initialHipsPosition;

    private void Start()
    {
        if (characterAnimator == null || characterAnimator.avatar == null ||
            !characterAnimator.avatar.isValid || !characterAnimator.avatar.isHuman)
        {
            Debug.LogError("EchoAvatar: 指定 Handyman_Full 的 Humanoid Animator。", this);
            enabled = false;
            return;
        }

        foreach (BoneDefinition definition in BoneDefinitions)
        {
            Transform target = characterAnimator.GetBoneTransform(definition.HumanBone);
            if (target == null)
                continue;

            bindings.Add(new BoneBinding
            {
                SourceIndex = definition.SourceIndex,
                Target = target,
                InitialLocalRotation = target.localRotation
            });
        }

        hips = characterAnimator.GetBoneTransform(HumanBodyBones.Hips);
        initialHipsPosition = hips.localPosition;
        Application.runInBackground = true;
        running = true;
        networkThread = new Thread(ReceiveLoop) { IsBackground = true };
        networkThread.Start();
        Debug.Log($"EchoAvatar: 對應 {bindings.Count} 個 Humanoid 骨骼，等待 localhost:{port}", this);
    }

    private void ReceiveLoop()
    {
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            Debug.Log($"EchoAvatar: TCP Server started on port {port}. Waiting for client connection...");

            while (running)
            {
                using (TcpClient connected = listener.AcceptTcpClient())
                {
                    client = connected;
                    connected.NoDelay = true;
                    Debug.Log("EchoAvatar: 推論程序已連上 Unity。");

                    try
                    {
                        using (NetworkStream stream = connected.GetStream())
                        {
                            while (running)
                            {
                                byte[] header = new byte[4];
                                if (!ReadExact(stream, header))
                                    break;

                                int length = (header[0] << 24) | (header[1] << 16) |
                                             (header[2] << 8) | header[3];
                                if (length <= 0 || length > MaxPacketBytes)
                                    throw new InvalidDataException($"Unexpected motion packet size: {length}");

                                byte[] body = new byte[length];
                                if (!ReadExact(stream, body))
                                    break;

                                MotionPacket packet = JsonConvert.DeserializeObject<MotionPacket>(
                                    Encoding.UTF8.GetString(body));
                                if (packet != null && packet.pose != null)
                                    receivedPackets.Enqueue(packet);
                            }
                        }
                    }
                    catch (Exception e) when (e is IOException || e is SocketException ||
                                              e is JsonException || e is InvalidDataException)
                    {
                        if (running)
                            Debug.LogWarning($"EchoAvatar: 動作連線中斷：{e.Message}");
                    }
                    finally
                    {
                        client = null;
                        if (running)
                            Debug.Log("EchoAvatar: 等待推論程序重新連線。");
                    }
                }
            }
        }
        catch (Exception e) when (e is SocketException || e is ObjectDisposedException)
        {
            if (running)
                Debug.LogError($"EchoAvatar: 無法啟動接收端：{e.Message}");
        }
        finally
        {
            listener?.Stop();
            listener = null;
        }
    }

    private static bool ReadExact(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = stream.Read(buffer, read, buffer.Length - read);
            if (count == 0)
                return false;
            read += count;
        }
        return true;
    }

    private void LateUpdate()
    {
        while (receivedPackets.TryDequeue(out MotionPacket packet))
        {
            for (int i = 0; i < packet.pose.Length; i++)
            {
                if (packet.pose[i] == null || packet.pose[i].Length != SourceBoneCount)
                    continue;

                playback.Enqueue(new MotionFrame
                {
                    Pose = packet.pose[i],
                    Translation = packet.trans != null && i < packet.trans.Length ? packet.trans[i] : null,
                    Blendshape = packet.blendshape != null && i < packet.blendshape.Length
                        ? packet.blendshape[i] : null
                });
            }
        }

        int limit = Mathf.Max(delayFrames + 1, maxBufferedFrames);
        while (playback.Count > limit)
            playback.Dequeue();

        if (playback.Count <= delayFrames)
            return;

        frameTimer += Time.deltaTime;
        while (frameTimer >= 1f / FrameRate && playback.Count > delayFrames)
        {
            ApplyFrame(playback.Dequeue());
            frameTimer -= 1f / FrameRate;
        }

        if (frameTimer > 1f)
            frameTimer = 0f;
    }

    private void ApplyFrame(MotionFrame frame)
    {
        foreach (BoneBinding binding in bindings)
        {
            float[] values = frame.Pose[binding.SourceIndex];
            if (values == null || values.Length < 4)
                continue;

            Quaternion delta = new Quaternion(values[0], values[1], values[2], values[3]);
            if (Quaternion.Dot(delta, delta) < 0.0001f)
                continue;
            delta = Quaternion.Normalize(delta);
            binding.Target.localRotation = binding.InitialLocalRotation *
                Quaternion.Slerp(Quaternion.identity, delta, rotationStrength);
        }

        if (applyRootTranslation && frame.Translation != null && frame.Translation.Length >= 3)
        {
            Vector3 translation = new Vector3(
                frame.Translation[0], frame.Translation[1], -frame.Translation[2]);
            hips.localPosition = initialHipsPosition + Quaternion.Euler(0f, 180f, 0f) *
                (translation / 100f);
        }

        if (applyFaceBlendshapes && faceRenderer != null && faceRenderer.sharedMesh != null &&
            frame.Blendshape != null)
        {
            int count = Mathf.Min(51, faceRenderer.sharedMesh.blendShapeCount,
                                  frame.Blendshape.Length);
            for (int i = 0; i < count; i++)
                faceRenderer.SetBlendShapeWeight(i, Mathf.Clamp01(frame.Blendshape[i]) * 100f);
        }
    }

    private void OnDisable()
    {
        running = false;
        client?.Close();
        listener?.Stop();
        if (networkThread != null && networkThread.IsAlive)
            networkThread.Join(500);
    }
}
